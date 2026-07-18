using System.Text;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Rag.Tests;

/// <summary>
/// 取込/再取込パイプライン。べき等性 (contentHash) / 文書差し替え / embedder 失敗時の無変更 /
/// 単一 Tx 原子性 / NEXT_CHUNK 連結 / DeleteDocument を検証する。
/// </summary>
public sealed class RagIngestTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public RagIngestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_rag3_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private const int Dim = 8;

    private RagStore NewStore(QuiverDatabase db, int target = 8, int overlap = 0) =>
        new(db, new RagStoreOptions
        {
            EmbeddingDimensions = Dim,
            Chunking = new ChunkingOptions { TargetSize = target, Overlap = overlap },
        });

    private static IngestedDocument Doc(string sourceId, params string[] paragraphs) =>
        new(sourceId, "title-" + sourceId,
            new Dictionary<string, string> { ["k"] = "v" },
            paragraphs.Select(p => new IngestedBlock(BlockKind.Paragraph, p)).ToArray());

    // ── 決定的フェイク embedder ──
    private sealed class FakeEmbedder : IChunkEmbedder
    {
        public int Dimensions { get; }
        public int CallCount;
        public FakeEmbedder(int dim) => Dimensions = dim;

        public ValueTask<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            CallCount++;
            var result = new float[texts.Count][];
            for (int i = 0; i < texts.Count; i++)
            {
                var v = new float[Dimensions];
                string s = texts[i];
                for (int j = 0; j < s.Length; j++) v[j % Dimensions] += (s[j] % 17) * 0.01f;
                if (v.All(x => x == 0f)) v[0] = 1f; // ゼロベクトル回避 (cosine)
                result[i] = v;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingEmbedder : IChunkEmbedder
    {
        public int Dimensions => Dim;
        public ValueTask<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    // ── 読み取りヘルパ ──
    private static (VertexId Doc, bool Found) FindDoc(IReadTransaction tx, string sourceId)
    {
        // 索引には削除済みの orphan エントリが残り得るので生存Vertexのみ採用する。
        var seek = tx.SeekIndex(RagSchema.DocSourceIndex, PropertyValue.FromString(sourceId));
        try
        {
            while (seek.MoveNext())
            {
                var hit = seek.Current;
                if (hit.Kind != EntityKind.Vertex)
                    continue;

                var vertex = new VertexId(hit.Value);
                if (tx.VertexExists(vertex))
                    return (vertex, true);
            }
        }
        finally { seek.Dispose(); }
        return (default, false);
    }

    private static List<VertexId> ChunkVertices(IReadTransaction tx, VertexId docId)
    {
        var ids = new List<VertexId>();
        var e = tx.EnumerateEdges(docId, Direction.Outgoing, RagSchema.HasChunkType);
        while (e.MoveNext()) ids.Add(e.Current.Target);
        return ids;
    }

    private static List<string> ChunkTextsOrdered(QuiverDatabase db, string sourceId)
    {
        using var tx = db.BeginReadTransaction();
        var (docId, found) = FindDoc(tx, sourceId);
        if (!found) return new();
        var items = new List<(int Ord, string Text)>();
        foreach (var c in ChunkVertices(tx, docId))
        {
            int ord = tx.GetProperty(c, RagSchema.PropOrdinal).Int32Value;
            string text = Encoding.UTF8.GetString(tx.GetProperty(c, RagSchema.PropText).Utf8StringValue);
            items.Add((ord, text));
        }
        return items.OrderBy(x => x.Ord).Select(x => x.Text).ToList();
    }

    private static int RelCount(QuiverDatabase db, string sourceId, string type)
    {
        using var tx = db.BeginReadTransaction();
        var (docId, found) = FindDoc(tx, sourceId);
        if (!found) return 0;
        // HAS_CHUNK は doc から、NEXT_CHUNK は各 chunk から数える。
        if (type == RagSchema.HasChunkType)
        {
            int n = 0;
            var e = tx.EnumerateEdges(docId, Direction.Outgoing, RagSchema.HasChunkType);
            while (e.MoveNext()) n++;
            return n;
        }
        int next = 0;
        foreach (var c in ChunkVertices(tx, docId))
        {
            var e = tx.EnumerateEdges(c, Direction.Outgoing, RagSchema.NextChunkType);
            while (e.MoveNext()) next++;
        }
        return next;
    }

    private static string DocTitle(QuiverDatabase db, string sourceId)
    {
        using var tx = db.BeginReadTransaction();
        var (docId, found) = FindDoc(tx, sourceId);
        if (!found) return "";
        return Encoding.UTF8.GetString(tx.GetProperty(docId, RagSchema.PropTitle).Utf8StringValue);
    }

    private static int KnnHitCount(QuiverDatabase db, string indexName, int k)
    {
        var q = new float[Dim];
        q[0] = 1f;
        using var tx = db.BeginReadTransaction();
        using var cursor = tx.KnnSearch(indexName, q, k);
        int n = 0;
        while (cursor.MoveNext()) n++;
        return n;
    }

    // ── テスト ──

    [Fact]
    public async Task Ingest_creates_document_and_chunks()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        var embedder = new FakeEmbedder(Dim);

        var r = await store.UpsertDocumentAsync(Doc("d1", "alpha", "bravo"), embedder);

        r.Unchanged.Should().BeFalse();
        r.ChunkCount.Should().Be(2);
        embedder.CallCount.Should().Be(1);

        ChunkTextsOrdered(db, "d1").Should().Equal("alpha", "bravo");
        RelCount(db, "d1", RagSchema.HasChunkType).Should().Be(2);
        RelCount(db, "d1", RagSchema.NextChunkType).Should().Be(1); // 連鎖は N-1
        KnnHitCount(db, store.VectorIndexName, 100).Should().Be(2);
    }

    [Fact]
    public async Task Same_document_twice_is_noop_and_skips_embedder()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        var embedder = new FakeEmbedder(Dim);
        var doc = Doc("d1", "alpha", "bravo");

        var r1 = await store.UpsertDocumentAsync(doc, embedder);
        var r2 = await store.UpsertDocumentAsync(doc, embedder);

        r1.Unchanged.Should().BeFalse();
        r2.Unchanged.Should().BeTrue();
        r2.ChunkCount.Should().Be(2);
        embedder.CallCount.Should().Be(1); // 2 回目は embed しない
        ChunkTextsOrdered(db, "d1").Should().Equal("alpha", "bravo");
    }

    [Fact]
    public async Task Reingest_replaces_old_chunks_and_removes_stale_vectors()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        var embedder = new FakeEmbedder(Dim);

        await store.UpsertDocumentAsync(Doc("d1", "alpha", "bravo"), embedder);

        // 旧チャンクVertex ID を控える。
        List<VertexId> oldChunks;
        using (var tx = db.BeginReadTransaction())
        {
            var (docId, _) = FindDoc(tx, "d1");
            oldChunks = ChunkVertices(tx, docId);
        }
        oldChunks.Should().HaveCount(2);

        // 内容変更で再取込。
        var r = await store.UpsertDocumentAsync(Doc("d1", "charlie", "delta", "echo"), embedder);

        r.Unchanged.Should().BeFalse();
        r.ChunkCount.Should().Be(3);
        ChunkTextsOrdered(db, "d1").Should().Equal("charlie", "delta", "echo");

        // 旧チャンクVertexは消えている。
        using (var tx = db.BeginReadTransaction())
        {
            foreach (var old in oldChunks)
                tx.VertexExists(old).Should().BeFalse();
        }

        // ベクトルも stale が残っていない (live = 3 件のみ)。
        KnnHitCount(db, store.VectorIndexName, 100).Should().Be(3);
    }

    [Fact]
    public async Task Embedder_exception_leaves_db_unchanged()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);

        // まず正常な版を入れる。
        await store.UpsertDocumentAsync(Doc("d1", "alpha", "bravo"), new FakeEmbedder(Dim));

        // 変更版を throwing embedder で再取込 → 例外。
        var act = async () => await store.UpsertDocumentAsync(Doc("d1", "charlie", "delta"), new ThrowingEmbedder());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        // 旧版が完全に残っている (中間状態なし)。
        ChunkTextsOrdered(db, "d1").Should().Equal("alpha", "bravo");
        KnnHitCount(db, store.VectorIndexName, 100).Should().Be(2);
    }

    [Fact]
    public async Task Title_only_change_is_noop_and_keeps_old_title()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        var blocks = new[] { new IngestedBlock(BlockKind.Paragraph, "same body text") };
        var meta = new Dictionary<string, string>();

        await store.UpsertDocumentAsync(
            new IngestedDocument("d1", "Original Title", meta, blocks), new FakeEmbedder(Dim));

        // Blocks 不変・title だけ変更 → contentHash 一致で no-op (設計どおり title は更新されない)。
        var r = await store.UpsertDocumentAsync(
            new IngestedDocument("d1", "Changed Title", meta, blocks), new FakeEmbedder(Dim));

        r.Unchanged.Should().BeTrue();
        DocTitle(db, "d1").Should().Be("Original Title");
    }

    [Fact]
    public async Task Replace_nonempty_document_with_empty_clears_chunks()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        await store.UpsertDocumentAsync(Doc("d1", "alpha", "bravo"), new FakeEmbedder(Dim));
        ChunkTextsOrdered(db, "d1").Should().HaveCount(2);

        var empty = new IngestedDocument("d1", "Empty", new Dictionary<string, string>(), Array.Empty<IngestedBlock>());
        var r = await store.UpsertDocumentAsync(empty, new FakeEmbedder(Dim));

        r.Unchanged.Should().BeFalse();
        r.ChunkCount.Should().Be(0);
        ChunkTextsOrdered(db, "d1").Should().BeEmpty();
        KnnHitCount(db, store.VectorIndexName, 100).Should().Be(0);
    }

    [Fact]
    public async Task Dimension_mismatch_throws()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);

        var act = async () => await store.UpsertDocumentAsync(Doc("d1", "x"), new FakeEmbedder(Dim + 1));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Delete_document_removes_everything()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        await store.UpsertDocumentAsync(Doc("d1", "alpha", "bravo"), new FakeEmbedder(Dim));

        store.DeleteDocument("d1").Should().BeTrue();

        using (var tx = db.BeginReadTransaction())
            FindDoc(tx, "d1").Found.Should().BeFalse();
        KnnHitCount(db, store.VectorIndexName, 100).Should().Be(0);
    }

    [Fact]
    public void Delete_nonexistent_returns_false()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        store.DeleteDocument("missing").Should().BeFalse();
    }

    [Fact]
    public async Task Next_chunk_chain_is_linear_and_ordered()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db, target: 3); // 2 文字段落 + 区切り 2 で 6 > 3 → 各段落が独立チャンク
        await store.UpsertDocumentAsync(Doc("d1", "aa", "bb", "cc", "dd"), new FakeEmbedder(Dim));

        // チャンク 4 件、HAS_CHUNK 4・NEXT_CHUNK 3。
        RelCount(db, "d1", RagSchema.HasChunkType).Should().Be(4);
        RelCount(db, "d1", RagSchema.NextChunkType).Should().Be(3);

        // ordinal 0 から NEXT_CHUNK を辿ると全チャンクを順に訪問できる。
        using var tx = db.BeginReadTransaction();
        var (docId, _) = FindDoc(tx, "d1");
        var byOrd = new Dictionary<long, int>();
        VertexId start = default;
        foreach (var c in ChunkVertices(tx, docId))
        {
            int ord = tx.GetProperty(c, RagSchema.PropOrdinal).Int32Value;
            byOrd[c.Value] = ord;
            if (ord == 0) start = c;
        }

        var visited = new List<int> { byOrd[start.Value] };
        var cur = start;
        while (true)
        {
            var e = tx.EnumerateEdges(cur, Direction.Outgoing, RagSchema.NextChunkType);
            if (!e.MoveNext()) break;
            cur = e.Current.Target;
            visited.Add(byOrd[cur.Value]);
        }
        visited.Should().Equal(0, 1, 2, 3);
    }

    [Fact]
    public async Task Large_document_ingests_in_single_pass()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db, target: 50, overlap: 10);
        var embedder = new FakeEmbedder(Dim);

        // 長文 1 段落 (~2000 char) を分割させる。
        string big = string.Concat(Enumerable.Range(0, 400).Select(i => "word" + (i % 10) + " "));
        var doc = new IngestedDocument("big", "Big", new Dictionary<string, string>(),
            new[] { new IngestedBlock(BlockKind.Paragraph, big) });

        var r = await store.UpsertDocumentAsync(doc, embedder);

        r.ChunkCount.Should().BeGreaterThan(10);
        RelCount(db, "big", RagSchema.HasChunkType).Should().Be(r.ChunkCount);
        RelCount(db, "big", RagSchema.NextChunkType).Should().Be(r.ChunkCount - 1);
        KnnHitCount(db, store.VectorIndexName, r.ChunkCount + 50).Should().Be(r.ChunkCount);
    }

    [Fact]
    public async Task Empty_document_creates_doc_with_no_chunks_and_skips_embedder()
    {
        using var db = QuiverDatabase.Open(_path);
        var store = NewStore(db);
        var embedder = new FakeEmbedder(Dim);
        var empty = new IngestedDocument("e", "Empty", new Dictionary<string, string>(), Array.Empty<IngestedBlock>());

        var r = await store.UpsertDocumentAsync(empty, embedder);

        r.Unchanged.Should().BeFalse();
        r.ChunkCount.Should().Be(0);
        embedder.CallCount.Should().Be(0); // チャンクが無いので embed しない
        ChunkTextsOrdered(db, "e").Should().BeEmpty();

        // 再取込はべき等 (no-op)。
        var r2 = await store.UpsertDocumentAsync(empty, embedder);
        r2.Unchanged.Should().BeTrue();
    }

    [Fact]
    public async Task Reopen_persists_chunks_and_vectors()
    {
        using (var db = QuiverDatabase.Open(_path))
        {
            var store = NewStore(db);
            await store.UpsertDocumentAsync(Doc("d1", "alpha", "bravo"), new FakeEmbedder(Dim));
        }
        using (var db = QuiverDatabase.Open(_path))
        {
            var store = NewStore(db); // 既存索引を踏んで再構築 (冪等)
            ChunkTextsOrdered(db, "d1").Should().Equal("alpha", "bravo");
            KnnHitCount(db, store.VectorIndexName, 100).Should().Be(2);
        }
    }
}
