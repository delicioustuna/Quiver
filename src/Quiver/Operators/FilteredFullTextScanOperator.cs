using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// FTS-4 graph-first counterpart of <see cref="FullTextScanOperator"/>: drains an
/// upstream NodeId-producing operator into a candidate set, then runs BM25 over
/// only those candidates and emits the top-<c>k</c> in descending relevance order.
/// The upstream is usually a label / property filter on a node scan — the candidate
/// side of a <c>g.Search(...).HasLabel(...)</c> push-down or an explicit
/// <c>.FilterByText(...)</c>.
/// </summary>
/// <remarks>
/// Pairs with <see cref="FilteredKnnNodeSourceOperator"/>
/// (vector graph-first). The shared <see cref="Bm25Scorer"/> computes df / idf over
/// the full postings list, so a candidate document's score is identical to the
/// text-first path; only the cut to top-k differs (it is taken <em>after</em>
/// restricting to candidates, which avoids the "k starvation" of text-first +
/// post-filter). Candidate ids are sequence-space (pipeline convention); the scorer
/// matches them against the posting sequence. Score is intentionally not surfaced.
/// </remarks>
internal sealed class FilteredFullTextScanOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceNodeColumn;
    private readonly string _indexName;
    private readonly string _queryText;
    private readonly int _k;
    private readonly Bm25CorpusStats? _corpus;
    private NodeId[] _results = Array.Empty<NodeId>();
    private int _pos = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FilteredFullTextScanOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        string indexName,
        string queryText,
        int k,
        Bm25CorpusStats? corpus = null)
    {
        if (string.IsNullOrEmpty(indexName))
            throw new ArgumentException("Full-text index name must not be empty.", nameof(indexName));
        ArgumentNullException.ThrowIfNull(queryText);
        if (k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sourceNodeColumn = sourceNodeColumn;
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
        // Validate the index up-front so a missing index throws symmetrically with the
        // text-first operator, regardless of candidate count (and before the drain).
        if (!tx.Indexes.TryGetFullTextIndex(_indexName, out var ft))
            throw new ConstraintException($"Full-text index '{_indexName}' does not exist.");
        var tokenizer = tx.Indexes.ResolveTokenizer(ft.TokenizerId);

        _source.Open(tx);
        var candidates = new HashSet<long>();
        while (_source.MoveNext())
        {
            var slot = _source.Current[_sourceNodeColumn];
            if (slot.Type == TupleSlotType.NodeId) candidates.Add(slot.LongValue);
        }
        if (candidates.Count == 0)
        {
            _results = Array.Empty<NodeId>();
            _pos = -1;
            return;
        }

        var (n, avgdl) = Bm25Scorer.ResolveCorpus(ft, _corpus);
        // FTS-8: use the snapshot df (when present) so a candidate's per-doc score matches
        // the text-first WAND path exactly (spec: 07_fulltext.md#wand parity). Graph-first stays a
        // candidate-bounded full scan — WAND targets the unbounded text-first cost.
        var ranked = Bm25Scorer.Rank(ft, tokenizer, _queryText, n, avgdl, candidates, _corpus?.Terms);

        _results = IndexValueResolver.ResolveLiveNodeIds(ranked, tx.Nodes).Take(_k).ToArray();
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

    public void Dispose() => _source.Dispose();
}
