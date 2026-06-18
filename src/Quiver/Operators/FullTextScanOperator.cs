using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// leaf operator: streams the top-<c>k</c> node ids from a full-text index
/// in descending BM25 relevance order, so it composes with the existing
/// filter / expand chain exactly like <see cref="KnnNodeSourceOperator"/>.
/// </summary>
/// <remarks>
/// Term-at-a-time BM25 via the shared <see cref="Bm25Scorer"/>:
/// tokenize the query with the index's recorded tokenizer, range-scan each term's
/// postings, accumulate per doc (df counted during the scan, idf applied per term),
/// then emit the top-k. Score is intentionally not surfaced (same MVP policy as KNN).
/// Visibility uses the same generation-match regime as the secondary-index seek path
/// (<see cref="IndexValueResolver"/>): false-positive postings whose slot was reused
/// are dropped. N/avgdl come from the carried <see cref="Bm25CorpusStats"/> when the
/// DSL had GraphStats; otherwise they are approximated from the norms index.
/// k1=1.2 / b=0.75 (design defaults).
/// </remarks>
internal sealed class FullTextScanOperator : IPhysicalOperator
{
    private readonly string _indexName;
    private readonly string _queryText;
    private readonly int _k;
    private readonly Bm25CorpusStats? _corpus;
    private NodeId[] _results = Array.Empty<NodeId>();
    private int _pos = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FullTextScanOperator(string indexName, string queryText, int k, Bm25CorpusStats? corpus = null)
    {
        if (string.IsNullOrEmpty(indexName))
            throw new ArgumentException("Full-text index name must not be empty.", nameof(indexName));
        ArgumentNullException.ThrowIfNull(queryText);
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");
        _indexName = indexName;
        _queryText = queryText;
        _k = k;
        _corpus = corpus;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        if (!tx.Indexes.TryGetFullTextIndex(_indexName, out var ft))
            throw new ConstraintException($"Full-text index '{_indexName}' does not exist.");
        var tokenizer = tx.Indexes.ResolveTokenizer(ft.TokenizerId);

        var (n, avgdl) = Bm25Scorer.ResolveCorpus(ft, _corpus);

        var nodes = tx.Nodes;
        var termStats = _corpus?.Terms;
        List<long>? ranked;

        if (FtsQueryParser.ContainsWildcard(_queryText))
        {
            var terms = FtsQueryParser.ParseAndExpand(_queryText, tokenizer, ft);
            ranked = termStats is not null
                ? Bm25Scorer.RankWandTerms(ft, terms, n, avgdl, termStats, _k,
                    isLive: packed => IndexValueResolver.IsLiveNode(packed, nodes))
                : null;
            ranked ??= Bm25Scorer.RankTerms(ft, terms, n, avgdl, candidateSequences: null, termStats);
        }
        else
        {
            ranked = termStats is not null
                ? Bm25Scorer.RankWand(ft, tokenizer, _queryText, n, avgdl, termStats, _k,
                    isLive: packed => IndexValueResolver.IsLiveNode(packed, nodes))
                : null;
            ranked ??= Bm25Scorer.Rank(ft, tokenizer, _queryText, n, avgdl, candidateSequences: null, termStats);
        }

        _results = IndexValueResolver.ResolveLiveNodeIds(ranked, nodes).Take(_k).ToArray();
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
}
