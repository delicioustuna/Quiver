using System.Text;
using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Text;

namespace Quiver.Query.Physical;

/// <summary>
/// FTS-8: per-term corpus statistics for WAND pruning. Holds the
/// snapshot <c>term → (df, maxTf)</c> table plus the corpus minimum document length, all
/// computed once at <see cref="Quiver.GraphStats"/> collection time. df drives idf (and the
/// per-term WAND upper bound).
/// <para>
/// 監査 #3: WAND の per-term 上限はもはや maxTf / minDocLen を使わない (snapshot がライブ index に
/// 対して stale だと過小評価され top-k を取りこぼすため)。代わりに tf/docLen に依らない漸近上限
/// <c>idf*(K1+1)</c> を使う。maxTf / minDocLen は将来の block-max WAND 等のために収集を残す。
/// </para>
/// </summary>
internal sealed class Bm25TermStats
{
    private readonly Dictionary<string, (int Df, int MaxTf)> _terms;

    public Bm25TermStats(Dictionary<string, (int Df, int MaxTf)> terms, int minDocLen)
    {
        _terms = terms;
        MinDocLen = minDocLen;
    }

    /// <summary>Smallest document length in the corpus (token count); 0 if empty.</summary>
    public int MinDocLen { get; }

    /// <summary>Number of distinct terms tracked.</summary>
    public int TermCount => _terms.Count;

    /// <summary>Document frequency / max term frequency for <paramref name="term"/>.</summary>
    public bool TryGet(string term, out int df, out int maxTf)
    {
        if (_terms.TryGetValue(term, out var v)) { df = v.Df; maxTf = v.MaxTf; return true; }
        df = 0; maxTf = 0; return false;
    }
}

/// <summary>
/// FTS-4: corpus-level BM25 statistics — document count N and average document
/// length — snapshotted from <see cref="Quiver.GraphStats"/>. When carried on a
/// <c>FullTextScanOp</c> the operator uses these instead of re-scanning the norms
/// index on every query (BM25 is robust to stat staleness, so a
/// periodically-collected approximation is fine).
/// <para>
/// FTS-8: when <see cref="Terms"/> is non-null the operator can use WAND document-at-a-time
/// pruning (per-term upper bounds from the snapshot) instead of the full term-at-a-time
/// scan; otherwise it falls back to the full scan with exact df.
/// </para>
/// </summary>
internal readonly record struct Bm25CorpusStats(
    long DocumentCount, double AverageDocLength, Bm25TermStats? Terms = null);

/// <summary>
/// FTS-4: shared term-at-a-time BM25 accumulator used by both the text-first
/// (<see cref="FullTextScanOperator"/>) and graph-first
/// (<see cref="FilteredFullTextScanOperator"/>) operators, so a candidate
/// document scores identically on either path — that is what makes the graph-first
/// rewrite rank-equivalent to text-first + post-filter.
/// FTS-8 adds <see cref="RankWand"/>, an exact-top-k WAND variant used by the
/// text-first path when per-term snapshot stats are available.
/// </summary>
internal static class Bm25Scorer
{
    /// <summary>BM25 term-frequency saturation parameter (standard default).</summary>
    public const double K1 = 1.2;

    /// <summary>BM25 document-length normalization parameter (standard default).</summary>
    public const double B = 0.75;

    /// <summary>
    /// Rank documents for <paramref name="queryText"/> by descending BM25 score
    /// (ties broken by ascending packed entityId for determinism). The returned ids
    /// are the raw packed entityIds (generation + sequence); the caller resolves
    /// them to live <c>NodeId</c>s.
    /// <para>
    /// <paramref name="candidateSequences"/> non-null restricts accumulation to that
    /// set (graph-first). Membership is tested on the posting's <em>sequence</em>
    /// (<see cref="EntityRef.Sequence"/>) because pipeline node ids are sequence-space
    /// while postings keys are packed. df / idf are computed over the <em>full</em>
    /// postings list (or, when <paramref name="termStats"/> is supplied, from the
    /// snapshot df so this path stays score-consistent with the WAND path).
    /// </para>
    /// </summary>
    public static List<long> Rank(
        FullTextIndex ft, ITokenizer tokenizer, string queryText,
        long n, double avgdl, HashSet<long>? candidateSequences, Bm25TermStats? termStats = null)
    {
        var sink = new TermSink();
        tokenizer.Tokenize(queryText, sink);
        if (sink.Terms.Count == 0) return new List<long>();

        var scores = new Dictionary<long, double>();
        var docLenCache = new Dictionary<long, int>();
        foreach (var term in sink.Terms)
        {
            var postings = ft.GetPostings(term);
            // Use the snapshot df when present so both query paths agree on idf; otherwise
            // count from the materialized postings (exact df, FTS-3 behaviour).
            int df = termStats is not null && termStats.TryGet(term, out var sdf, out _) ? sdf : postings.Count;
            if (df == 0) continue;
            double idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));
            foreach (var (eid, tf) in postings)
            {
                if (candidateSequences is not null && !candidateSequences.Contains(EntityRef.Sequence(eid)))
                    continue;
                if (!docLenCache.TryGetValue(eid, out var dl))
                {
                    dl = ft.TryGetDocLength(eid, out var d) ? d : 0;
                    docLenCache[eid] = dl;
                }
                double denom = tf + K1 * (1.0 - B + (avgdl > 0 ? B * dl / avgdl : 0.0));
                double contrib = denom > 0 ? idf * (tf * (K1 + 1.0)) / denom : 0.0;
                scores[eid] = scores.TryGetValue(eid, out var prev) ? prev + contrib : contrib;
            }
        }

        return SortByScore(scores);
    }

    /// <summary>
    /// FTS-8: exact top-<paramref name="k"/> BM25 via WAND document-at-a-time pruning.
    /// Uses per-term snapshot df + upper bounds to skip postings of
    /// high-df terms once they cannot beat the current k-th best score, advancing lagging
    /// cursors with B+Tree <c>SeekTo</c>. Returns the ranked packed entityIds, or
    /// <c>null</c> when a query term is absent from <paramref name="termStats"/> (unbounded
    /// → caller falls back to <see cref="Rank"/> for safety).
    /// <para>
    /// <paramref name="isLive"/> (when supplied) drops dead / slot-reused postings before
    /// they enter the heap, so the heap holds the top-k <em>live</em> documents — the same
    /// "resolve, then take k" visibility the full-scan operator applies (the heap is bounded
    /// to k, so post-hoc filtering could shrink the result below k).
    /// </para>
    /// </summary>
    public static List<long>? RankWand(
        FullTextIndex ft, ITokenizer tokenizer, string queryText,
        long n, double avgdl, Bm25TermStats termStats, int k, Func<long, bool>? isLive = null)
    {
        var sink = new TermSink();
        tokenizer.Tokenize(queryText, sink);
        if (sink.Terms.Count == 0) return new List<long>();

        var cursors = new List<WandTerm>(sink.Terms.Count);
        foreach (var term in sink.Terms)
        {
            if (!termStats.TryGet(term, out int df, out _))
                return null; // unknown term: cannot bound safely → fall back to full scan
            if (df <= 0) continue;
            double idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));
            // 監査 #3 (WAND staleness, spec: 07_fulltext.md#wand): per-term の上限は BM25 項寄与の
            // 漸近上限 idf*(K1+1) を使う。寄与 idf*(tf*(K1+1))/(tf + K1*lenNorm) は tf について単調増加で
            // tf→∞ で idf*(K1+1) に収束し、lenNorm≥0 なので任意の tf/docLen に対し ≤ idf*(K1+1)。
            // これは snapshot の maxTf/minDocLen に依存しないため、snapshot 後にライブ index へ高 tf /
            // 短文書が増えても上限が過小評価されず、WAND は full-scan と厳密一致する (top-k の取りこぼし無し)。
            // maxTf/minDocLen ベースのより緊い上限は staleness で不正となり得たため不採用。
            double ub = idf * (K1 + 1.0);
            var cur = ft.OpenPostingsCursor(Encoding.UTF8.GetBytes(term));
            if (cur.MoveNext()) cursors.Add(new WandTerm(idf, ub, cur));
        }
        if (cursors.Count == 0) return new List<long>();

        var heap = new BoundedTopK(k);
        while (true)
        {
            for (int i = cursors.Count - 1; i >= 0; i--)
                if (cursors[i].Cursor.Exhausted) cursors.RemoveAt(i);
            if (cursors.Count == 0) break;

            cursors.Sort(static (a, b) => a.Cursor.CurrentEid.CompareTo(b.Cursor.CurrentEid));
            double theta = heap.IsFull ? heap.MinScore : double.NegativeInfinity;

            // pivot = smallest index whose cumulative upper bound exceeds theta.
            double cum = 0;
            int pivot = -1;
            for (int i = 0; i < cursors.Count; i++)
            {
                cum += cursors[i].Ub;
                if (cum > theta) { pivot = i; break; }
            }
            if (pivot < 0) break; // no remaining document can beat the k-th best score

            long pivotEid = cursors[pivot].Cursor.CurrentEid;
            if (cursors[0].Cursor.CurrentEid == pivotEid)
            {
                int docLen = ft.TryGetDocLength(pivotEid, out var dl) ? dl : 0;
                double lenNorm = 1.0 - B + (avgdl > 0 ? B * docLen / avgdl : 0.0);
                double score = 0;
                for (int i = 0; i < cursors.Count; i++)
                {
                    if (cursors[i].Cursor.CurrentEid != pivotEid) continue;
                    int tf = cursors[i].Cursor.CurrentTf;
                    double denom = tf + K1 * lenNorm;
                    if (denom > 0) score += cursors[i].Idf * (tf * (K1 + 1.0)) / denom;
                    cursors[i].Cursor.MoveNext();
                }
                if (isLive is null || isLive(pivotEid))
                    heap.Offer(pivotEid, score);
            }
            else
            {
                // Advance the smallest (lagging) cursor up to the pivot document.
                cursors[0].Cursor.SeekTo(pivotEid);
            }
        }

        return heap.ToRankedEids();
    }

    /// <summary>
    /// Resolve N / avgdl: use the carried <paramref name="corpus"/> snapshot when it
    /// has documents (FTS-4 GraphStats path, avoids the O(N) norms scan), otherwise
    /// fall back to a one-shot norms summary (FTS-3 behaviour).
    /// </summary>
    public static (long N, double Avgdl) ResolveCorpus(FullTextIndex ft, Bm25CorpusStats? corpus)
    {
        if (corpus is { DocumentCount: > 0 } c)
            return (c.DocumentCount, c.AverageDocLength);
        long n = ft.DocumentCount;
        var (_, totalTokens) = ft.NormsSummary();
        double avgdl = n > 0 ? (double)totalTokens / n : 0.0;
        return (n, avgdl);
    }

    private static List<long> SortByScore(Dictionary<long, double> scores)
    {
        var ranked = new List<KeyValuePair<long, double>>(scores);
        ranked.Sort(static (a, b) =>
        {
            int byScore = b.Value.CompareTo(a.Value);
            return byScore != 0 ? byScore : a.Key.CompareTo(b.Key);
        });
        var result = new List<long>(ranked.Count);
        foreach (var kv in ranked) result.Add(kv.Key);
        return result;
    }

    private readonly struct WandTerm
    {
        public WandTerm(double idf, double ub, PostingsCursor cursor)
        {
            Idf = idf; Ub = ub; Cursor = cursor;
        }

        public double Idf { get; }
        public double Ub { get; }
        public PostingsCursor Cursor { get; }
    }

    /// <summary>
    /// Bounded top-k by (score desc, entityId asc) — the same total order as
    /// <see cref="SortByScore"/>. <see cref="MinScore"/> is WAND's threshold θ once full.
    /// k is tiny (tens), so the worst-entry search is a linear scan.
    /// </summary>
    private sealed class BoundedTopK
    {
        private readonly int _k;
        private readonly List<(long Eid, double Score)> _items;

        public BoundedTopK(int k) { _k = k; _items = new List<(long, double)>(k); }

        public bool IsFull => _items.Count >= _k;

        /// <summary>Lowest score currently retained (the k-th best); valid only when full.</summary>
        public double MinScore { get; private set; }

        public void Offer(long eid, double score)
        {
            if (_items.Count < _k)
            {
                _items.Add((eid, score));
                if (_items.Count == _k) RecomputeMin();
                return;
            }
            // Find the worst retained entry: lowest score, breaking ties by largest eid
            // (so equal-score entries keep the smaller eid, matching SortByScore).
            int worst = 0;
            for (int i = 1; i < _items.Count; i++)
            {
                if (_items[i].Score < _items[worst].Score ||
                    (_items[i].Score == _items[worst].Score && _items[i].Eid > _items[worst].Eid))
                    worst = i;
            }
            var w = _items[worst];
            if (score > w.Score || (score == w.Score && eid < w.Eid))
            {
                _items[worst] = (eid, score);
                RecomputeMin();
            }
        }

        private void RecomputeMin()
        {
            double min = double.PositiveInfinity;
            foreach (var it in _items) if (it.Score < min) min = it.Score;
            MinScore = min;
        }

        public List<long> ToRankedEids()
        {
            _items.Sort(static (a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                return byScore != 0 ? byScore : a.Eid.CompareTo(b.Eid);
            });
            var result = new List<long>(_items.Count);
            foreach (var it in _items) result.Add(it.Eid);
            return result;
        }
    }

    private sealed class TermSink : ITokenSink
    {
        public HashSet<string> Terms { get; } = new(StringComparer.Ordinal);
        public void Accept(ReadOnlySpan<char> token) => Terms.Add(token.ToString());
    }
}
