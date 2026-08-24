using System.Text;
using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Rag.Tests;

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
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_rag3_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.yata");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private const int Dim = 8;
    private const string EmbeddingProfileId = "fake-embedding-v1";

    private RagStore NewStore(YatagarasuDatabase db, int target = 8, int overlap = 0) =>
        new(db, new RagStoreOptions
        {
            EmbeddingDimensions = Dim,
            IngestionProfile = new RagIngestionProfile
            {
                EmbeddingProfileId = EmbeddingProfileId,
            },
            Chunking = new ChunkingOptions { TargetSize = target, Overlap = overlap },
        });

    private static IngestedDocument Doc(string sourceId, params string[] paragraphs) =>
        new(sourceId, "title-" + sourceId,
            new Dictionary<string, string> { ["k"] = "v" },
            paragraphs.Select(p => new IngestedBlock(BlockKind.Paragraph, p)).ToArray());

    // ── 決定的フェイク embedder ──
    private sealed class FakeEmbedder : IChunkEmbedder
    {
        public string ProfileId { get; }
        public int Dimensions { get; }
        public int CallCount;
        public IReadOnlyList<string> LastInputs { get; private set; } = Array.Empty<string>();
        public FakeEmbedder(int dim, string profileId = EmbeddingProfileId)
        {
            Dimensions = dim;
            ProfileId = profileId;
        }

        public ValueTask<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            CallCount++;
            LastInputs = texts.ToArray();
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
        public string ProfileId => EmbeddingProfileId;
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

    private static List<string> ChunkTextsOrdered(YatagarasuDatabase db, string sourceId)
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

    private static int RelCount(YatagarasuDatabase db, string sourceId, string type)
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

    private static string DocTitle(YatagarasuDatabase db, string sourceId)
    {
        using var tx = db.BeginReadTransaction();
        var (docId, found) = FindDoc(tx, sourceId);
        if (!found) return "";
        return Encoding.UTF8.GetString(tx.GetProperty(docId, RagSchema.PropTitle).Utf8StringValue);
    }

    private static string DocStringProperty(YatagarasuDatabase db, string sourceId, string propertyKey)
    {
        using var tx = db.BeginReadTransaction();
        var (docId, found) = FindDoc(tx, sourceId);
        if (!found || !tx.HasProperty(docId, propertyKey)) return string.Empty;
        return Encoding.UTF8.GetString(tx.GetProperty(docId, propertyKey).Utf8StringValue);
    }

    private static int KnnHitCount(YatagarasuDatabase db, string indexName, int k)
    {
        var q = new float[Dim];
        q[0] = 1f;
        using var tx = db.BeginReadTransaction();
        using var cursor = tx.KnnSearch(indexName, q, k);
        int n = 0;
        while (cursor.MoveNext()) n++;
        return n;
    }

    private static int ScalarIndexHitCount(YatagarasuDatabase db, string indexName, string value)
    {
        using var tx = db.BeginReadTransaction();
        var cursor = tx.SeekIndex(indexName, PropertyValue.FromString(value));
        int count = 0;
        try
        {
            while (cursor.MoveNext())
                if (cursor.Current.Kind == EntityKind.Vertex) count++;
        }
        finally
        {
            cursor.Dispose();
        }
        return count;
    }

    // ── テスト ──

    [Fact]
    public async Task Ingest_creates_document_and_chunks()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var store = NewStore(db);
        var embedder = new FakeEmbedder(Dim);

        var r = await store.UpsertDocumentAsync(Doc("d1", "alpha", "bravo"), embedder);

        r.Unchanged.Should().BeFalse();
        r.ChunkCount.Should().Be(2);
        r.ReplacedDocumentVertexId.Should().BeNull();
        embedder.CallCount.Should().Be(1);

        ChunkTextsOrdered(db, "d1").Should().Equal("alpha", "bravo");
        RelCount(db, "d1", RagSchema.HasChunkType).Should().Be(2);
        RelCount(db, "d1", RagSchema.NextChunkType).Should().Be(1); // 連鎖は N-1
        KnnHitCount(db, store.VectorIndexName, 100).Should().Be(2);
    }

    [Fact]
    public async Task Same_document_twice_is_noop_and_skips_embedder()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var store = NewStore(db);
        var embedder = new FakeEmbedder(Dim);
        var doc = Doc("d1", "alpha", "bravo");

        var r1 = await store.UpsertDocumentAsync(doc, embedder);
        var r2 = await store.UpsertDocumentAsync(doc, embedder);

        r1.Unchanged.Should().BeFalse();
        r2.Unchanged.Should().BeTrue();
        r2.ChunkCount.Should().Be(2);
        r2.DocumentVertexId.Should().Be(r1.DocumentVertexId);
        r2.ReplacedDocumentVertexId.Should().BeNull();
        embedder.CallCount.Should().Be(1); // 2 回目は embed しない
        ChunkTextsOrdered(db, "d1").Should().Equal("alpha", "bravo");
    }

    [Fact]
    public async Task Reingest_replaces_document_id_cascades_relations_and_removes_stale_chunks_and_vectors()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var store = NewStore(db);
        var embedder = new FakeEmbedder(Dim);

        UpsertResult initial =
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
        VertexId anchor;
        NexusId nexus;
        using (var tx = db.BeginWriteTransaction())
        {
            anchor = tx.CreateVertex("Anchor");
            tx.CreateEdge(
                initial.DocumentVertexId.ToCore(),
                anchor,
                "USER_LINK");
            nexus = tx.CreateNexus("USER_CONTEXT", [
                new("Document", initial.DocumentVertexId.ToCore()),
                new("Anchor", anchor),
            ]);
            tx.Commit();
        }

        var r = await store.UpsertDocumentAsync(
            Doc("d1", "charlie", "delta", "echo"),
            embedder);

        r.Unchanged.Should().BeFalse();
        r.ChunkCount.Should().Be(3);
        r.DocumentVertexId.Should().NotBe(initial.DocumentVertexId);
        r.ReplacedDocumentVertexId.Should().Be(initial.DocumentVertexId);
        ChunkTextsOrdered(db, "d1").Should().Equal("charlie", "delta", "echo");

        // 旧チャンクVertexは消えている。
        using (var tx = db.BeginReadTransaction())
        {
            foreach (var old in oldChunks)
                tx.VertexExists(old).Should().BeFalse();
            tx.VertexExists(initial.DocumentVertexId.ToCore()).Should().BeFalse();
            tx.VertexExists(r.DocumentVertexId.ToCore()).Should().BeTrue();
            var fromNew = tx.EnumerateEdges(
                r.DocumentVertexId.ToCore(),
                Direction.Outgoing,
                "USER_LINK");
            fromNew.MoveNext().Should().BeFalse(
                "利用者 Edge は新しい Document ID へ暗黙継承しない");
            var anchorEdges = tx.EnumerateEdges(
                anchor,
                Direction.Both,
                "USER_LINK");
            anchorEdges.MoveNext().Should().BeFalse();
            var members = tx.GetMembers(nexus);
            members.MoveNext().Should().BeFalse(
                "旧 Document が参加した Nexus は置換境界で cascade される");
        }

        // ベクトルも stale が残っていない (live = 3 件のみ)。
        KnnHitCount(db, store.VectorIndexName, 100).Should().Be(3);
    }

    [Fact]
    public async Task Embedder_exception_leaves_db_unchanged()
    {
        using var db = YatagarasuDatabase.Open(_path);
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
    public async Task Title_only_change_updates_attributes_without_embedding()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var store = NewStore(db);
        var blocks = new[] { new IngestedBlock(BlockKind.Paragraph, "same body text") };
        var meta = new Dictionary<string, string>();

        var embedder = new FakeEmbedder(Dim);
        UpsertResult initial = await store.UpsertDocumentAsync(
            new IngestedDocument("d1", "Original Title", meta, blocks), embedder);

        var r = await store.UpsertDocumentAsync(
            new IngestedDocument("d1", "Changed Title", meta, blocks), embedder);

        r.Disposition.Should().Be(RagUpsertDisposition.AttributesUpdated);
        r.Unchanged.Should().BeFalse();
        r.DocumentVertexId.Should().Be(initial.DocumentVertexId);
        r.ReplacedDocumentVertexId.Should().BeNull();
        embedder.CallCount.Should().Be(1);
        DocTitle(db, "d1").Should().Be("Changed Title");
    }

    [Fact]
    public async Task Metadata_and_revision_change_update_same_document_without_embedding()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var store = NewStore(db);
        var embedder = new FakeEmbedder(Dim);
        var blocks = new[] { new IngestedBlock(BlockKind.Paragraph, "same body text") };
        var first = new IngestedDocument(
            "d1",
            "Title",
            new Dictionary<string, string> { ["category"] = "old" },
            blocks)
        {
            ContentRevision = "rev-1",
        };
        UpsertResult initial = await store.UpsertDocumentAsync(first, embedder);

        var updated = first with
        {
            Metadata = new Dictionary<string, string> { ["category"] = "new" },
            ContentRevision = "rev-2",
        };
        UpsertResult result = await store.UpsertDocumentAsync(updated, embedder);

        result.Disposition.Should().Be(RagUpsertDisposition.AttributesUpdated);
        result.DocumentVertexId.Should().Be(initial.DocumentVertexId);
        result.ReplacedDocumentVertexId.Should().BeNull();
        embedder.CallCount.Should().Be(1);
        DocStringProperty(db, "d1", RagSchema.PropContentRevision).Should().Be("rev-2");
        DocStringProperty(db, "d1", RagSchema.PropMetadataJson)
            .Should().Be("{\"category\":\"new\"}");
    }

    [Fact]
    public async Task Metadata_key_order_does_not_change_fingerprint()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var store = NewStore(db);
        var embedder = new FakeEmbedder(Dim);
        var blocks = new[] { new IngestedBlock(BlockKind.Paragraph, "same body text") };

        await store.UpsertDocumentAsync(
            new IngestedDocument(
                "d1",
                "Title",
                new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" },
                blocks),
            embedder);
        UpsertResult result = await store.UpsertDocumentAsync(
            new IngestedDocument(
                "d1",
                "Title",
                new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" },
                blocks),
            embedder);

        result.Disposition.Should().Be(RagUpsertDisposition.Unchanged);
        embedder.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Embedder_profile_mismatch_is_rejected_before_embedding_or_database_change()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var store = NewStore(db);
        var embedder = new FakeEmbedder(Dim, "different-model-v2");

        var act = async () => await store.UpsertDocumentAsync(Doc("d1", "alpha"), embedder);

        await act.Should().ThrowAsync<RagIngestionProfileMismatchException>();
        embedder.CallCount.Should().Be(0);
        using var tx = db.BeginReadTransaction();
        FindDoc(tx, "d1").Found.Should().BeFalse();
    }

    [Fact]
    public async Task Chunk_text_embedding_template_excludes_heading_path()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var options = new RagStoreOptions
        {
            EmbeddingDimensions = Dim,
            IngestionProfile = new RagIngestionProfile
            {
                EmbeddingProfileId = EmbeddingProfileId,
                EmbeddingInputTemplate = RagEmbeddingInputTemplate.ChunkText,
            },
            Chunking = new ChunkingOptions { TargetSize = 40, Overlap = 0 },
        };
        var store = new RagStore(db, options);
        var embedder = new FakeEmbedder(Dim);
        var document = new IngestedDocument(
            "d1",
            "Title",
            new Dictionary<string, string>(),
            [
                new IngestedBlock(BlockKind.Heading, "Section", HeadingLevel: 1),
                new IngestedBlock(BlockKind.Paragraph, "body"),
            ]);

        await store.UpsertDocumentAsync(document, embedder);

        embedder.LastInputs.Should().Equal("body");
    }

    [Fact]
    public async Task Promoted_metadata_property_and_index_follow_attribute_updates()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var store = new RagStore(db, new RagStoreOptions
        {
            EmbeddingDimensions = Dim,
            IngestionProfile = new RagIngestionProfile
            {
                EmbeddingProfileId = EmbeddingProfileId,
            },
            MetadataIndexes =
            [
                new RagMetadataIndex("category", "ragMetadata.category", "idx_rag_metadata_category"),
            ],
            Chunking = new ChunkingOptions { TargetSize = 40, Overlap = 0 },
        });
        var embedder = new FakeEmbedder(Dim);
        var blocks = new[] { new IngestedBlock(BlockKind.Paragraph, "same body") };
        var first = new IngestedDocument(
            "d1",
            "Title",
            new Dictionary<string, string> { ["category"] = "old" },
            blocks);
        UpsertResult initial = await store.UpsertDocumentAsync(first, embedder);

        ScalarIndexHitCount(db, "idx_rag_metadata_category", "old").Should().Be(1);
        var changed = await store.UpsertDocumentAsync(
            first with
            {
                Metadata = new Dictionary<string, string> { ["category"] = "new" },
            },
            embedder);

        changed.Disposition.Should().Be(RagUpsertDisposition.AttributesUpdated);
        changed.DocumentVertexId.Should().Be(initial.DocumentVertexId);
        ScalarIndexHitCount(db, "idx_rag_metadata_category", "old").Should().Be(0);
        ScalarIndexHitCount(db, "idx_rag_metadata_category", "new").Should().Be(1);

        var removed = await store.UpsertDocumentAsync(
            first with { Metadata = new Dictionary<string, string>() },
            embedder);
        removed.Disposition.Should().Be(RagUpsertDisposition.AttributesUpdated);
        ScalarIndexHitCount(db, "idx_rag_metadata_category", "new").Should().Be(0);
        embedder.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Promoted_metadata_seek_work_tracks_selectivity_instead_of_document_count()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var store = new RagStore(db, new RagStoreOptions
        {
            EmbeddingDimensions = Dim,
            IngestionProfile = new RagIngestionProfile
            {
                EmbeddingProfileId = EmbeddingProfileId,
            },
            MetadataIndexes =
            [
                new RagMetadataIndex("rare", "ragMetadata.rare", "idx_rag_metadata_rare"),
                new RagMetadataIndex("medium", "ragMetadata.medium", "idx_rag_metadata_medium"),
                new RagMetadataIndex("all", "ragMetadata.all", "idx_rag_metadata_all"),
            ],
        });
        var embedder = new FakeEmbedder(Dim);
        for (int i = 0; i < 100; i++)
        {
            var metadata = new Dictionary<string, string> { ["all"] = "yes" };
            if (i < 4) metadata["rare"] = "yes";
            if (i < 25) metadata["medium"] = "yes";
            await store.UpsertDocumentAsync(
                new IngestedDocument(
                    $"d{i:D3}",
                    $"Document {i}",
                    metadata,
                    Array.Empty<IngestedBlock>()),
                embedder);
        }

        using (var tx = db.BeginReadTransaction())
            tx.Query.Vertices().HasLabel(RagSchema.DocumentLabel).ToList().Should().HaveCount(100);
        ScalarIndexHitCount(db, "idx_rag_metadata_rare", "yes").Should().Be(4);
        ScalarIndexHitCount(db, "idx_rag_metadata_medium", "yes").Should().Be(25);
        ScalarIndexHitCount(db, "idx_rag_metadata_all", "yes").Should().Be(100);
        embedder.CallCount.Should().Be(0, "empty documents do not require embedding");
    }

    [Fact]
    public async Task Replace_nonempty_document_with_empty_clears_chunks()
    {
        using var db = YatagarasuDatabase.Open(_path);
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
        using var db = YatagarasuDatabase.Open(_path);
        var store = NewStore(db);

        var act = async () => await store.UpsertDocumentAsync(Doc("d1", "x"), new FakeEmbedder(Dim + 1));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Delete_document_removes_everything()
    {
        using var db = YatagarasuDatabase.Open(_path);
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
        using var db = YatagarasuDatabase.Open(_path);
        var store = NewStore(db);
        store.DeleteDocument("missing").Should().BeFalse();
    }

    [Fact]
    public async Task Next_chunk_chain_is_linear_and_ordered()
    {
        using var db = YatagarasuDatabase.Open(_path);
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
        using var db = YatagarasuDatabase.Open(_path);
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
        using var db = YatagarasuDatabase.Open(_path);
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
        using (var db = YatagarasuDatabase.Open(_path))
        {
            var store = NewStore(db);
            await store.UpsertDocumentAsync(Doc("d1", "alpha", "bravo"), new FakeEmbedder(Dim));
        }
        using (var db = YatagarasuDatabase.Open(_path))
        {
            var store = NewStore(db); // 既存索引を踏んで再構築 (冪等)
            ChunkTextsOrdered(db, "d1").Should().Equal("alpha", "bravo");
            KnnHitCount(db, store.VectorIndexName, 100).Should().Be(2);
        }
    }
}
