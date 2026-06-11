using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Storage.Records;
using Quiver.Text;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// FTS-3 leaf operator: streams the top-<c>k</c> node ids from a full-text index
/// in descending BM25 relevance order, so it composes with the existing
/// filter / expand chain exactly like <see cref="KnnNodeSourceOperator"/>.
/// </summary>
/// <remarks>
/// design 13 section 6/7. Term-at-a-time: tokenize the query with the index's
/// recorded tokenizer, range-scan each term's postings, accumulate BM25 per doc
/// (df counted during the scan, idf applied per term), then emit the top-k.
/// Score is intentionally not surfaced (same MVP policy as KNN). Visibility uses
/// the same generation-match regime as the secondary-index seek path
/// (<see cref="IndexValueResolver"/>): false-positive postings whose slot was
/// reused are dropped. N/avgdl are an approximation here; FTS-4 moves them to
/// GraphStats. k1=1.2 / b=0.75 (design defaults).
/// </remarks>
internal sealed class FullTextScanOperator : IPhysicalOperator
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly string _indexName;
    private readonly string _queryText;
    private readonly int _k;
    private NodeId[] _results = Array.Empty<NodeId>();
    private int _pos = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FullTextScanOperator(string indexName, string queryText, int k)
    {
        if (string.IsNullOrEmpty(indexName))
            throw new ArgumentException("Full-text index name must not be empty.", nameof(indexName));
        ArgumentNullException.ThrowIfNull(queryText);
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");
        _indexName = indexName;
        _queryText = queryText;
        _k = k;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        if (!tx.Indexes.TryGetFullTextIndex(_indexName, out var ft))
            throw new ConstraintException($"Full-text index '{_indexName}' does not exist.");
        var tokenizer = tx.Indexes.ResolveTokenizer(ft.TokenizerId);

        var sink = new TermSink();
        tokenizer.Tokenize(_queryText, sink);
        if (sink.Terms.Count == 0)
        {
            _results = Array.Empty<NodeId>();
            _pos = -1;
            return;
        }

        long n = ft.DocumentCount;
        var (_, totalTokens) = ft.NormsSummary();
        double avgdl = n > 0 ? (double)totalTokens / n : 0.0;

        // term-at-a-time BM25 accumulation (df determined while scanning each term).
        var scores = new Dictionary<long, double>();
        var docLenCache = new Dictionary<long, int>();
        foreach (var term in sink.Terms)
        {
            var postings = ft.GetPostings(term);
            int df = postings.Count;
            if (df == 0) continue;
            double idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));
            foreach (var (eid, tf) in postings)
            {
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

        // Rank by descending score, then resolve to live node ids (generation match,
        // preserving rank order) and take k. Dead / slot-reused entries are dropped.
        var ranked = new List<KeyValuePair<long, double>>(scores);
        ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
        _results = IndexValueResolver
            .ResolveLiveNodeIds(ranked.Select(kv => kv.Key), tx.Nodes)
            .Take(_k)
            .ToArray();
        _pos = -1;
    }

    public bool MoveNext()
    {
        if (_pos + 1 >= _results.Length) return false;
        _pos++;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _results[_pos].Value };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose() { }

    private sealed class TermSink : ITokenSink
    {
        public HashSet<string> Terms { get; } = new(StringComparer.Ordinal);
        public void Accept(ReadOnlySpan<char> token) => Terms.Add(token.ToString());
    }
}
