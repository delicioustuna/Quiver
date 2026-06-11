using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Text;

namespace Quiver.Query.Physical;

/// <summary>
/// FTS-4: corpus-level BM25 statistics — document count N and average document
/// length — snapshotted from <see cref="Quiver.GraphStats"/>. When carried on a
/// <c>FullTextScanOp</c> the operator uses these instead of re-scanning the norms
/// index on every query (design 13 §6: BM25 is robust to stat staleness, so a
/// periodically-collected approximation is fine).
/// </summary>
internal readonly record struct Bm25CorpusStats(long DocumentCount, double AverageDocLength);

/// <summary>
/// FTS-4: shared term-at-a-time BM25 accumulator used by both the text-first
/// (<see cref="FullTextScanOperator"/>) and graph-first
/// (<see cref="FilteredFullTextScanOperator"/>) operators, so a candidate
/// document scores identically on either path — that is what makes the graph-first
/// rewrite rank-equivalent to text-first + post-filter (design 13 §7.1).
/// </summary>
internal static class Bm25Scorer
{
    /// <summary>BM25 term-frequency saturation parameter (design 13 §6 default).</summary>
    public const double K1 = 1.2;

    /// <summary>BM25 document-length normalization parameter (design 13 §6 default).</summary>
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
    /// while postings keys are packed. df / idf are still computed over the <em>full</em>
    /// postings list so candidate scores match the text-first path exactly.
    /// </para>
    /// </summary>
    public static List<long> Rank(
        FullTextIndex ft, ITokenizer tokenizer, string queryText,
        long n, double avgdl, HashSet<long>? candidateSequences)
    {
        var sink = new TermSink();
        tokenizer.Tokenize(queryText, sink);
        if (sink.Terms.Count == 0) return new List<long>();

        var scores = new Dictionary<long, double>();
        var docLenCache = new Dictionary<long, int>();
        foreach (var term in sink.Terms)
        {
            var postings = ft.GetPostings(term);
            int df = postings.Count;                 // global df (corpus-wide), matches text-first idf
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

    private sealed class TermSink : ITokenSink
    {
        public HashSet<string> Terms { get; } = new(StringComparer.Ordinal);
        public void Accept(ReadOnlySpan<char> token) => Terms.Add(token.ToString());
    }
}
