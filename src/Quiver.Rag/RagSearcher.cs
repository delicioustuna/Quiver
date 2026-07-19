using System.Text;
using System.Text.Json;
using Quiver.Api;
using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Query.Physical;
using Quiver.Storage.Records;

namespace Quiver.Rag;

/// <summary>
/// ハイブリッド検索 (BM25 + KNN を RRF 融合) と graph expansion (隣接チャンク連結・親文書付与) を 1 API で返す検索器。
/// score はエンジンの BM25 / vector scorer と RRF 実装から受け取り、隣接チャンク連結後も代表ヒットの内訳を保持する。
/// </summary>
public sealed class RagSearcher
{
    private readonly RagStore _store;

    /// <summary>指定の <see cref="RagStore"/> 上に検索器を構築する。</summary>
    /// <param name="store">対象ストア。</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> が null。</exception>
    public RagSearcher(RagStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// ハイブリッド検索を実行し、隣接連結済みの上位ヒットを返す。
    /// <paramref name="queryVector"/> が null/空なら BM25 のみ、<paramref name="queryText"/> が空
    /// (または全文索引無効) なら KNN のみ、両方あれば RRF 融合。両方とも使えないときは空を返す。
    /// </summary>
    /// <param name="queryText">全文検索クエリ (空可)。</param>
    /// <param name="queryVector">問い合わせベクトル (null 可)。次元は索引と一致していること。</param>
    /// <param name="options">検索オプション。<c>null</c> なら既定値。</param>
    public IReadOnlyList<RagHit> Search(
        string queryText, float[]? queryVector, RagSearchOptions? options = null)
    {
        options ??= new RagSearchOptions();
        if (options.K < 1) return Array.Empty<RagHit>();

        bool hasText = !string.IsNullOrWhiteSpace(queryText) && _store.FullTextEnabled;
        bool hasVector = queryVector is { Length: > 0 };
        if (!hasText && !hasVector) return Array.Empty<RagHit>();

        var db = _store.Database;
        using var tx = db.BeginReadTransaction();
        var g = tx.Query;

        // 1) MetadataEquals があれば一致文書のチャンクに母集団を絞ってから
        //    engine scorerへ渡し、top-k と score 内訳を同じ母集団から得る。
        List<RankedChunk> ranked =
            RankHits(tx, g, queryText, queryVector, hasText, hasVector, options);
        if (ranked.Count == 0) return Array.Empty<RagHit>();

        // 2) 各ヒットを展開し、MetadataFilter で文書単位に絞る。
        //    同一文書に複数ヒットしても metadataJson を 1 回しかパースしないようキャッシュする。
        var expanded = new List<ExpandedHit>(ranked.Count);
        var metaCache = new Dictionary<long, RagMetadata>();
        for (int i = 0; i < ranked.Count; i++)
        {
            RankedChunk rankedChunk = ranked[i];
            var center = rankedChunk.VertexId;
            if (!TryReadChunk(tx, center, out var centerInfo)) continue;

            var docId = FindDocumentOf(tx, center);
            if (docId is null) continue;
            var doc = docId.Value;

            if (options.MetadataFilter is not null)
            {
                if (!metaCache.TryGetValue(doc.Value, out var meta))
                    metaCache[doc.Value] = meta = ReadMetadata(tx, doc);
                if (!options.MetadataFilter(meta)) continue;
            }

            var neighborhood = ExpandNeighborhood(tx, center, centerInfo, options.NeighborExpansion);
            expanded.Add(new ExpandedHit(
                i + 1,
                center,
                doc,
                centerInfo.HeadingPath,
                neighborhood,
                rankedChunk.Score));
        }

        // 3) 文書単位で隣接ヒットをマージし、ランク順に整列して返す。
        return MergeHits(tx, expanded, options);
    }

    // ── ランク取得 (global / candidate-side push-down) ──

    /// <summary>
    /// ランク順のチャンク VertexId を取得する。<see cref="RagSearchOptions.MetadataEquals"/> があれば
    /// 一致文書のチャンクへ母集団を絞った candidate-side 検索 (graph-first) に切替え、無ければ
    /// 通常の text-first / vector-first / hybrid 検索を行う。
    /// </summary>
    private List<RankedChunk> RankHits(
        IReadTransaction tx, GraphTraversalSource g,
        string queryText, float[]? queryVector, bool hasText, bool hasVector, RagSearchOptions options)
    {
        VertexId[]? candidates = null;
        if (options.MetadataEquals is { Count: > 0 } equals)
        {
            candidates = CollectCandidateChunks(tx, g, equals);
            if (candidates.Length == 0) return [];
        }

        List<ChannelHit> text = hasText
            ? ScoreText(tx, queryText, options.K, candidates)
            : [];
        List<ChannelHit> vector = hasVector
            ? ScoreVector(tx, queryVector!, options.K, candidates)
            : [];

        if (!hasVector)
            return text.Select(static hit => new RankedChunk(
                    hit.VertexId,
                    new RagScore(
                        hit.Score,
                        null,
                        hit.Score,
                        RagFusionMethod.TextOnly,
                        0)))
                .ToList();
        if (!hasText)
            return vector.Select(static hit => new RankedChunk(
                    hit.VertexId,
                    new RagScore(
                        null,
                        (float)hit.Score,
                        hit.Score,
                        RagFusionMethod.VectorOnly,
                        0)))
                .ToList();

        var textScores = text.ToDictionary(
            static hit => hit.VertexId.Value,
            static hit => hit.Score);
        var vectorScores = vector.ToDictionary(
            static hit => hit.VertexId.Value,
            static hit => hit.Score);
        IReadOnlyList<IReadOnlyList<long>> channels =
        [
            text.Select(static hit => hit.VertexId.Value).ToArray(),
            vector.Select(static hit => hit.VertexId.Value).ToArray(),
        ];
        // RAG 側で式と定数を複製するとエンジンの fusion 順位と乖離するため、共有プリミティブを使う。
        return ReciprocalRankFusion.Fuse(channels, options.K)
            .Select(result => new RankedChunk(
                new VertexId(result.EntityId),
                new RagScore(
                    textScores.TryGetValue(result.EntityId, out double bm25)
                        ? bm25
                        : null,
                    vectorScores.TryGetValue(result.EntityId, out double similarity)
                        ? (float)similarity
                        : null,
                    result.Score,
                    RagFusionMethod.ReciprocalRankFusion,
                    ReciprocalRankFusion.RankConstant)))
            .ToList();
    }

    private static List<ChannelHit> ScoreText(
        IReadTransaction tx,
        string queryText,
        int k,
        IReadOnlyList<VertexId>? candidates)
    {
        var inner = tx.AsInternal().Inner;
        if (inner.FullTextSegments is null
            || !inner.FullTextSegments.TryOpen(
                inner,
                RagSchema.ChunkTextIndex,
                out FullTextSegmentSnapshot snapshot))
            return [];

        HashSet<long>? candidateSequences = candidates is null
            ? null
            : candidates.Select(static id => id.Sequence).ToHashSet();
        HashSet<long>? candidateIds = candidates is null
            ? null
            : candidates.Select(static id => EntityRef.Pack(
                    EntityKind.Vertex,
                    id.Sequence,
                    id.Generation))
                .ToHashSet();
        (long n, double avgdl) = Bm25Scorer.ResolveCorpus(snapshot, null);
        List<Bm25Score> scored;
        if (FtsQueryParser.ContainsBooleanOps(queryText))
        {
            ParsedFtsQuery parsed = FtsQueryParser.ParseBooleanAndExpand(
                queryText,
                snapshot.Tokenizer,
                snapshot);
            scored = Bm25Scorer.RankBooleanScored(
                snapshot,
                parsed,
                n,
                avgdl,
                candidateSequences);
        }
        else if (FtsQueryParser.ContainsWildcard(queryText)
                 || FtsQueryParser.ContainsFuzzy(queryText))
        {
            IReadOnlySet<string> terms = FtsQueryParser.ParseAndExpand(
                queryText,
                snapshot.Tokenizer,
                snapshot);
            scored = Bm25Scorer.RankTermsScored(
                snapshot,
                terms,
                n,
                avgdl,
                candidateSequences);
        }
        else
        {
            scored = Bm25Scorer.RankScored(
                snapshot,
                snapshot.Tokenizer,
                queryText,
                n,
                avgdl,
                candidateSequences);
        }

        var result = new List<ChannelHit>(Math.Min(k, scored.Count));
        foreach (Bm25Score hit in scored)
        {
            if (candidateIds is not null
                && !candidateIds.Contains(hit.PackedEntityId))
                continue;
            var id = VertexId.Create(
                EntityRef.UnpackSequence(hit.PackedEntityId),
                EntityRef.UnpackGeneration(hit.PackedEntityId));
            if (!snapshot.IsVisibleVertexCandidate(hit.PackedEntityId, inner)
                || !tx.VertexExists(id))
                continue;
            result.Add(new(id, hit.Score));
            if (result.Count == k)
                break;
        }
        return result;
    }

    private List<ChannelHit> ScoreVector(
        IReadTransaction tx,
        float[] queryVector,
        int k,
        IReadOnlyList<VertexId>? candidates)
    {
        if (candidates is null)
        {
            var result = new List<ChannelHit>(k);
            using VectorSearchCursor cursor = tx.KnnSearch(
                _store.VectorIndexName,
                queryVector,
                k);
            while (cursor.MoveNext())
                if (cursor.Current.Owner.Kind == EntityKind.Vertex)
                    result.Add(new(
                        new VertexId(cursor.Current.Owner.Value),
                        cursor.Current.Score));
            return result;
        }

        var scored = new List<ChannelHit>(candidates.Count);
        var vector = new float[_store.Options.EmbeddingDimensions];
        foreach (VertexId candidate in candidates)
        {
            if (!tx.TryGetVectorProperty(
                    EntityRef.From(candidate),
                    RagSchema.PropEmbedding,
                    vector))
                continue;
            scored.Add(new(
                candidate,
                VectorMetrics.Score(
                    _store.Options.VectorMetric,
                    queryVector,
                    vector)));
        }
        return scored
            .OrderByDescending(static hit => hit.Score)
            .ThenBy(static hit => hit.VertexId.Value)
            .Take(k)
            .ToList();
    }

    /// <summary>
    /// <paramref name="equals"/> の全キーが一致する Document のチャンク VertexId を集める。
    /// Document ラベルスキャン (LabelVertexIndex があれば O(|Document|)) 1 回 + 各文書の HAS_CHUNK 列挙。
    /// </summary>
    private static VertexId[] CollectCandidateChunks(
        IReadTransaction tx, GraphTraversalSource g, IReadOnlyDictionary<string, string> equals)
    {
        var cands = new List<VertexId>();
        foreach (var docId in g.Vertices().HasLabel(RagSchema.DocumentLabel).ToList())
        {
            if (!tx.VertexExists(docId)) continue;
            if (!MatchesMetadataEquals(tx, docId, equals)) continue;
            var e = tx.EnumerateEdges(docId, Direction.Outgoing, RagSchema.HasChunkType);
            while (e.MoveNext())
                if (tx.VertexExists(e.Current.Target)) cands.Add(e.Current.Target);
        }
        return cands.ToArray();
    }

    /// <summary>文書の metadataJson が <paramref name="equals"/> の全キーを期待値で満たすか (AND)。</summary>
    private static bool MatchesMetadataEquals(
        IReadTransaction tx, VertexId docId, IReadOnlyDictionary<string, string> equals)
    {
        var meta = ParseMetadataJson(ReadStringOrEmpty(tx, docId, RagSchema.PropMetadataJson));
        foreach (var kv in equals)
            if (!meta.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        return true;
    }

    // ── 内部表現 ──
    private readonly record struct ChannelHit(VertexId VertexId, double Score);
    private readonly record struct RankedChunk(VertexId VertexId, RagScore Score);
    private readonly record struct ChunkInfo(
        VertexId VertexId, int Ordinal, string Text, int CharStart, int CharEnd, string HeadingPath);

    private sealed record ExpandedHit(
        int Rank,
        VertexId Center,
        VertexId DocId,
        string HeadingPath,
        List<ChunkInfo> Chunks,
        RagScore Score);

    // ── ヒット展開 ──

    private static bool TryReadChunk(IReadTransaction tx, VertexId id, out ChunkInfo info)
    {
        info = default;
        if (!tx.VertexExists(id) || !tx.HasProperty(id, RagSchema.PropText)) return false;
        string text = ReadString(tx, id, RagSchema.PropText);
        int ord = ReadInt(tx, id, RagSchema.PropOrdinal, 0);
        int cs = ReadInt(tx, id, RagSchema.PropCharStart, 0);
        int ce = ReadInt(tx, id, RagSchema.PropCharEnd, text.Length);
        string hp = tx.HasProperty(id, RagSchema.PropHeadingPath)
            ? ReadString(tx, id, RagSchema.PropHeadingPath) : string.Empty;
        info = new ChunkInfo(id, ord, text, cs, ce, hp);
        return true;
    }

    private static VertexId? FindDocumentOf(IReadTransaction tx, VertexId chunk)
    {
        var e = tx.EnumerateEdges(chunk, Direction.Incoming, RagSchema.HasChunkType);
        return e.MoveNext() ? e.Current.Source : null;
    }

    /// <summary>center から NEXT_CHUNK を前後 <paramref name="n"/> 件ずつ辿って近傍チャンクを集める。</summary>
    private static List<ChunkInfo> ExpandNeighborhood(
        IReadTransaction tx, VertexId center, ChunkInfo centerInfo, int n)
    {
        var list = new List<ChunkInfo> { centerInfo };
        if (n <= 0) return list;

        var cur = center;
        for (int i = 0; i < n; i++)
        {
            var prev = StepNextChunk(tx, cur, Direction.Incoming);
            if (prev is null) break;
            if (TryReadChunk(tx, prev.Value, out var info)) list.Add(info);
            cur = prev.Value;
        }

        cur = center;
        for (int i = 0; i < n; i++)
        {
            var next = StepNextChunk(tx, cur, Direction.Outgoing);
            if (next is null) break;
            if (TryReadChunk(tx, next.Value, out var info)) list.Add(info);
            cur = next.Value;
        }
        return list;
    }

    private static VertexId? StepNextChunk(IReadTransaction tx, VertexId vertex, Direction dir)
    {
        var e = tx.EnumerateEdges(vertex, dir, RagSchema.NextChunkType);
        if (!e.MoveNext()) return null;
        return dir == Direction.Incoming ? e.Current.Source : e.Current.Target;
    }

    // ── マージ ──

    private static List<RagHit> MergeHits(
        IReadTransaction tx, List<ExpandedHit> hits, RagSearchOptions options)
    {
        var result = new List<RagHit>();

        foreach (var grp in hits.GroupBy(h => h.DocId.Value))
        {
            // 近傍の ordinal 範囲が重なる/隣接するヒットを 1 クラスタへまとめる。
            var ordered = grp.OrderBy(h => h.Chunks.Min(c => c.Ordinal)).ToList();
            var clusters = new List<List<ExpandedHit>>();
            int clusterHi = int.MinValue;

            foreach (var h in ordered)
            {
                int lo = h.Chunks.Min(c => c.Ordinal);
                int hi = h.Chunks.Max(c => c.Ordinal);
                if (clusters.Count > 0 && lo <= clusterHi + 1)
                {
                    clusters[^1].Add(h);
                    clusterHi = Math.Max(clusterHi, hi);
                }
                else
                {
                    clusters.Add(new List<ExpandedHit> { h });
                    clusterHi = hi;
                }
            }

            foreach (var cluster in clusters)
            {
                var union = cluster
                    .SelectMany(x => x.Chunks)
                    .GroupBy(c => c.VertexId.Value)
                    .Select(gg => gg.First())
                    .OrderBy(c => c.Ordinal)
                    .ToList();

                var best = cluster.OrderBy(x => x.Rank).First();
                var docRef = options.IncludeDocument
                    ? ReadDocRef(tx, best.DocId)
                    : new RagDocumentRef(string.Empty, string.Empty);

                result.Add(new RagHit(
                    ConcatChunks(union),
                    best.HeadingPath,
                    docRef,
                    best.Rank,
                    best.Center,
                    best.Score));
            }
        }

        result.Sort((a, b) => a.Rank.CompareTo(b.Rank));
        return result;
    }

    /// <summary>
    /// ordinal 昇順のチャンク列を 1 本の本文へ連結する。
    /// チャンカーのオーバーラップは CharStart/CharEnd オフセットで除去し、ブロック区切りのギャップには区切りを補う。
    /// </summary>
    private static string ConcatChunks(List<ChunkInfo> chunks)
    {
        var sb = new StringBuilder();
        int cursor = 0;
        bool first = true;
        foreach (var c in chunks)
        {
            if (first)
            {
                sb.Append(c.Text);
                cursor = c.CharEnd;
                first = false;
                continue;
            }
            if (c.CharStart > cursor)
            {
                // span 外に区切りがある = 別ブロック境界。BlockSeparator を補う。
                // 同一ブロック内の連続分割は CharStart == cursor (半開区間) なので else に落ち、
                // skip==0 で本文全体がそのまま連結される (偽の区切りを入れない)。
                sb.Append(Chunker.BlockSeparator).Append(c.Text);
            }
            else
            {
                int skip = cursor - c.CharStart; // オーバーラップ分 (連続なら 0) を捨てる
                if (skip < c.Text.Length) sb.Append(c.Text.AsSpan(skip));
            }
            cursor = Math.Max(cursor, c.CharEnd);
        }
        return sb.ToString();
    }

    // ── プロパティ読み取り ──

    private static RagDocumentRef ReadDocRef(IReadTransaction tx, VertexId docId)
        => new(ReadStringOrEmpty(tx, docId, RagSchema.PropSourceId),
               ReadStringOrEmpty(tx, docId, RagSchema.PropTitle));

    private static RagMetadata ReadMetadata(IReadTransaction tx, VertexId docId)
        => new(ReadStringOrEmpty(tx, docId, RagSchema.PropSourceId),
               ReadStringOrEmpty(tx, docId, RagSchema.PropTitle),
               ParseMetadataJson(ReadStringOrEmpty(tx, docId, RagSchema.PropMetadataJson)));

    private static string ReadString(IReadTransaction tx, VertexId id, string key)
    {
        var pv = tx.GetProperty(id, key);
        return pv.Type == PropertyValueType.String ? Encoding.UTF8.GetString(pv.Utf8StringValue) : string.Empty;
    }

    private static string ReadStringOrEmpty(IReadTransaction tx, VertexId id, string key)
        => tx.HasProperty(id, key) ? ReadString(tx, id, key) : string.Empty;

    private static int ReadInt(IReadTransaction tx, VertexId id, string key, int fallback)
        => tx.HasProperty(id, key) ? tx.GetProperty(id, key).Int32Value : fallback;

    private static IReadOnlyDictionary<string, string> ParseMetadataJson(string json)
    {
        var dict = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(json)) return dict;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String)
                        dict[p.Name] = p.Value.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // 壊れた JSON は空メタとして扱う (検索を落とさない)。
        }
        return dict;
    }
}
