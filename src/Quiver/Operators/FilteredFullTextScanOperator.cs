using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// <see cref="FullTextScanOperator"/> の graph-first 対応版。上流の NodeId 生成演算子を
/// 候補セットに排出し、その候補のみに BM25 を適用して top-<c>k</c> を関連度降順で放出する。
/// 上流は通常ラベル / プロパティフィルタ付きノードスキャン
/// (<c>g.Search(...).HasLabel(...)</c> のプッシュダウンや <c>.FilterByText(...)</c>)。
/// </summary>
/// <remarks>
/// <see cref="FilteredKnnNodeSourceOperator"/> (ベクトル graph-first) と対をなす。
/// 共有 <see cref="Bm25Scorer"/> が全 posting リストで df / idf を計算するため、候補文書の
/// スコアは text-first 経路と同一。top-k の切り出しのみ異なり、候補に限定した後に行うため
/// text-first + post-filter の "k starvation" を回避する。候補 ID は sequence 空間
/// (パイプライン規約); スコアは意図的に非公開。
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
        // インデックスの存在を先行検証し、候補数に関わらず text-first 演算子と
        // 対称的に例外を投げる (drain の前に検証する)。
        if (!tx.Indexes.TryGetFullTextIndex(_indexName, out var ft))
            throw new ConstraintException($"Full-text index '{_indexName}' does not exist.");
        var tokenizer = tx.Indexes.ResolveTokenizer(ft.TokenizerId);

        _source.Open(tx);
        var candidates = new HashSet<long>();
        while (_source.MoveNext())
        {
            var slot = _source.Current[_sourceNodeColumn];
            if (slot.Type != TupleSlotType.NodeId)
                continue;

            using var node = tx.Nodes.Read(new NodeId(slot.LongValue));
            if (node.InUse)
                candidates.Add(node.Id.Sequence);
        }
        if (candidates.Count == 0)
        {
            _results = Array.Empty<NodeId>();
            _pos = -1;
            return;
        }

        var (n, avgdl) = Bm25Scorer.ResolveCorpus(ft, _corpus);

        List<long> ranked;
        if (FtsQueryParser.ContainsBooleanOps(_queryText))
        {
            var parsed = FtsQueryParser.ParseBooleanAndExpand(_queryText, tokenizer, ft);
            ranked = Bm25Scorer.RankBoolean(ft, parsed, n, avgdl, candidates, _corpus?.Terms);
        }
        else if (FtsQueryParser.ContainsWildcard(_queryText) || FtsQueryParser.ContainsFuzzy(_queryText))
        {
            var terms = FtsQueryParser.ParseAndExpand(_queryText, tokenizer, ft);
            ranked = Bm25Scorer.RankTerms(ft, terms, n, avgdl, candidates, _corpus?.Terms);
        }
        else
        {
            ranked = Bm25Scorer.Rank(ft, tokenizer, _queryText, n, avgdl, candidates, _corpus?.Terms);
        }

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
