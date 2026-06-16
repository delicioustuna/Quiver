using System.Text;
using System.Text.Json;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Rag;

/// <summary>
/// ハイブリッド検索 (BM25 + KNN を RRF 融合) と graph expansion (隣接チャンク連結・親文書付与) を
/// 1 API で返す検索器。クエリ実行はエンジン DSL (<c>g.HybridSearch</c> / <c>g.Knn</c> / <c>g.Search</c>) への
/// 薄い写像に徹し、融合ロジックは持たない。
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
    /// <paramref name="queryVector"/> が null/空なら BM25 のみ、<paramref name="queryText"/> が空 (または
    /// 全文索引無効) なら KNN のみ、両方あれば RRF 融合。両方とも使えないときは空を返す。
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
        using var tx = db.BeginReadOnlyTransaction();
        var g = tx.G(db.Schema);

        // 1) ランク順のチャンク NodeId を取得 (score は伝播しないので順位 = relevance)。
        //    MetadataEquals があれば一致文書のチャンクに母集団を絞ってから検索する (push-down)。
        List<NodeId> ranked = RankHits(tx, g, queryText, queryVector, hasText, hasVector, options);
        if (ranked.Count == 0) return Array.Empty<RagHit>();

        // 2) 各ヒットを展開し、MetadataFilter で文書単位に絞る。
        //    同一文書に複数ヒットしても metadataJson を 1 回しかパースしないようキャッシュする。
        var expanded = new List<ExpandedHit>(ranked.Count);
        var metaCache = new Dictionary<long, RagMetadata>();
        for (int i = 0; i < ranked.Count; i++)
        {
            var center = ranked[i];
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
            expanded.Add(new ExpandedHit(i + 1, center, doc, centerInfo.HeadingPath, neighborhood));
        }

        // 3) 文書単位で隣接ヒットをマージし、ランク順に整列して返す。
        return MergeHits(tx, expanded, options);
    }

    // ── ランク取得 (global / candidate-side push-down) ──

    /// <summary>
    /// ランク順のチャンク NodeId を取得する。<see cref="RagSearchOptions.MetadataEquals"/> があれば
    /// 一致文書のチャンクへ母集団を絞った candidate-side 検索 (graph-first) に切替え、無ければ
    /// 通常の text-first / vector-first / hybrid 検索を行う。
    /// </summary>
    private List<NodeId> RankHits(
        IGraphTransaction tx, GraphTraversalSource g,
        string queryText, float[]? queryVector, bool hasText, bool hasVector, RagSearchOptions options)
    {
        if (options.MetadataEquals is not { Count: > 0 } equals)
        {
            // push-down なし: エンジン DSL の global 検索へ薄く写像。
            if (hasText && hasVector)
                return g.HybridSearch(
                    RagSchema.ChunkTextIndex, queryText, _store.VectorIndexName, queryVector!, options.K).ToList();
            if (hasVector)
                return g.Knn(_store.VectorIndexName, queryVector!, options.K).ToList();
            return g.Search(RagSchema.ChunkTextIndex, queryText, options.K).ToList();
        }

        // メタデータ一致文書のチャンクだけを母集団にする (recall hole を避ける)。
        var cands = CollectCandidateChunks(tx, g, equals);
        if (cands.Length == 0) return new List<NodeId>();

        if (hasText && hasVector)
        {
            // candidate 制約付き hybrid: 各チャンネルを母集団内で求め RRF で融合する。
            var textRanked = g.Nodes(cands).FilterByText(RagSchema.ChunkTextIndex, queryText, options.K).ToList();
            var vecRanked = g.Nodes(cands).FilterByKnn(_store.VectorIndexName, queryVector!, options.K).ToList();
            return RrfFuse(textRanked, vecRanked, options.K);
        }
        if (hasVector)
            return g.Nodes(cands).FilterByKnn(_store.VectorIndexName, queryVector!, options.K).ToList();
        return g.Nodes(cands).FilterByText(RagSchema.ChunkTextIndex, queryText, options.K).ToList();
    }

    /// <summary>
    /// <paramref name="equals"/> の全キーが一致する Document のチャンク NodeId を集める。
    /// Document ラベルスキャン (LabelNodeIndex があれば O(|Document|)) 1 回 + 各文書の HAS_CHUNK 列挙。
    /// </summary>
    private static NodeId[] CollectCandidateChunks(
        IGraphTransaction tx, GraphTraversalSource g, IReadOnlyDictionary<string, string> equals)
    {
        var cands = new List<NodeId>();
        foreach (var docId in g.Nodes().HasLabel(RagSchema.DocumentLabel).ToList())
        {
            if (!tx.NodeExists(docId)) continue;
            if (!MatchesMetadataEquals(tx, docId, equals)) continue;
            var e = tx.EnumerateRelationships(docId, Direction.Outgoing, RagSchema.HasChunkType);
            while (e.MoveNext())
                if (tx.NodeExists(e.Current.Target)) cands.Add(e.Current.Target);
        }
        return cands.ToArray();
    }

    /// <summary>文書の metadataJson が <paramref name="equals"/> の全キーを期待値で満たすか (AND)。</summary>
    private static bool MatchesMetadataEquals(
        IGraphTransaction tx, NodeId docId, IReadOnlyDictionary<string, string> equals)
    {
        var meta = ParseMetadataJson(ReadStringOrEmpty(tx, docId, RagSchema.PropMetadataJson));
        foreach (var kv in equals)
            if (!meta.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
        return true;
    }

    /// <summary>
    /// 2 つのランク列を RRF (<c>Σ 1/(60 + rank)</c>, rank は 1 始まり) で融合し上位 <paramref name="k"/> 件を返す。
    /// candidate 制約付き hybrid 用。エンジン <c>FusionOperator</c> の RRF と同値だが、candidate-bearing な
    /// <c>g.HybridSearch</c> の DSL が無いため Rag 層で同じ定数 (k0=60) を用いて融合する。
    /// 同一チャンクは sequence 空間で名寄せし、同点は sequence 昇順で決定的に整列する。
    /// </summary>
    private static List<NodeId> RrfFuse(List<NodeId> textRanked, List<NodeId> vecRanked, int k)
    {
        const int K0 = 60;
        var score = new Dictionary<long, double>();
        var rep = new Dictionary<long, NodeId>();

        void Accumulate(List<NodeId> ranked)
        {
            for (int i = 0; i < ranked.Count; i++)
            {
                long key = EntityRef.Sequence(ranked[i].Value);
                score[key] = (score.TryGetValue(key, out var s) ? s : 0d) + 1d / (K0 + i + 1);
                rep[key] = ranked[i]; // 生存 NodeId を代表に保持 (どちらのチャンネルも downstream 読取可)
            }
        }

        Accumulate(vecRanked);
        Accumulate(textRanked); // text を後勝ちにし代表 NodeId を text チャンネル側へ寄せる

        return score
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Take(k)
            .Select(kv => rep[kv.Key])
            .ToList();
    }

    // ── 内部表現 ──
    private readonly record struct ChunkInfo(
        NodeId NodeId, int Ordinal, string Text, int CharStart, int CharEnd, string HeadingPath);

    private sealed record ExpandedHit(
        int Rank, NodeId Center, NodeId DocId, string HeadingPath, List<ChunkInfo> Chunks);

    // ── ヒット展開 ──

    private static bool TryReadChunk(IGraphTransaction tx, NodeId id, out ChunkInfo info)
    {
        info = default;
        if (!tx.NodeExists(id) || !tx.HasProperty(id, RagSchema.PropText)) return false;
        string text = ReadString(tx, id, RagSchema.PropText);
        int ord = ReadInt(tx, id, RagSchema.PropOrdinal, 0);
        int cs = ReadInt(tx, id, RagSchema.PropCharStart, 0);
        int ce = ReadInt(tx, id, RagSchema.PropCharEnd, text.Length);
        string hp = tx.HasProperty(id, RagSchema.PropHeadingPath)
            ? ReadString(tx, id, RagSchema.PropHeadingPath) : string.Empty;
        info = new ChunkInfo(id, ord, text, cs, ce, hp);
        return true;
    }

    private static NodeId? FindDocumentOf(IGraphTransaction tx, NodeId chunk)
    {
        var e = tx.EnumerateRelationships(chunk, Direction.Incoming, RagSchema.HasChunkType);
        return e.MoveNext() ? e.Current.Source : null;
    }

    /// <summary>center から NEXT_CHUNK を前後 <paramref name="n"/> 件ずつ辿って近傍チャンクを集める。</summary>
    private static List<ChunkInfo> ExpandNeighborhood(
        IGraphTransaction tx, NodeId center, ChunkInfo centerInfo, int n)
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

    private static NodeId? StepNextChunk(IGraphTransaction tx, NodeId node, Direction dir)
    {
        var e = tx.EnumerateRelationships(node, dir, RagSchema.NextChunkType);
        if (!e.MoveNext()) return null;
        return dir == Direction.Incoming ? e.Current.Source : e.Current.Target;
    }

    // ── マージ ──

    private static List<RagHit> MergeHits(
        IGraphTransaction tx, List<ExpandedHit> hits, RagSearchOptions options)
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
                    .GroupBy(c => c.NodeId.Value)
                    .Select(gg => gg.First())
                    .OrderBy(c => c.Ordinal)
                    .ToList();

                var best = cluster.OrderBy(x => x.Rank).First();
                var docRef = options.IncludeDocument
                    ? ReadDocRef(tx, best.DocId)
                    : new RagDocumentRef(string.Empty, string.Empty);

                result.Add(new RagHit(ConcatChunks(union), best.HeadingPath, docRef, best.Rank, best.Center));
            }
        }

        result.Sort((a, b) => a.Rank.CompareTo(b.Rank));
        return result;
    }

    /// <summary>
    /// ordinal 昇順のチャンク列を 1 本の本文へ連結する。チャンカーのオーバーラップは
    /// CharStart/CharEnd オフセットで除去し、ブロック区切りのギャップには区切りを補う。
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

    private static RagDocumentRef ReadDocRef(IGraphTransaction tx, NodeId docId)
        => new(ReadStringOrEmpty(tx, docId, RagSchema.PropSourceId),
               ReadStringOrEmpty(tx, docId, RagSchema.PropTitle));

    private static RagMetadata ReadMetadata(IGraphTransaction tx, NodeId docId)
        => new(ReadStringOrEmpty(tx, docId, RagSchema.PropSourceId),
               ReadStringOrEmpty(tx, docId, RagSchema.PropTitle),
               ParseMetadataJson(ReadStringOrEmpty(tx, docId, RagSchema.PropMetadataJson)));

    private static string ReadString(IGraphTransaction tx, NodeId id, string key)
    {
        var pv = tx.GetProperty(id, key);
        return pv.Type == PropertyValueType.String ? Encoding.UTF8.GetString(pv.Utf8StringValue) : string.Empty;
    }

    private static string ReadStringOrEmpty(IGraphTransaction tx, NodeId id, string key)
        => tx.HasProperty(id, key) ? ReadString(tx, id, key) : string.Empty;

    private static int ReadInt(IGraphTransaction tx, NodeId id, string key, int fallback)
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
