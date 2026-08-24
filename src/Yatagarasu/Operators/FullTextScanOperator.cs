using Yatagarasu.Core;
using Yatagarasu.Index.FullText;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// 全文検索インデックスから BM25 関連度降順で top-<c>k</c> Vertex ID を放出するリーフ演算子。
/// <see cref="KnnVertexSourceOperator"/> と同様にフィルタ / 展開チェーンと合成できる。
/// </summary>
/// <remarks>
/// 共有 <see cref="Bm25Scorer"/> による term-at-a-time BM25: インデックス記録済みトークナイザで
/// クエリをトークン化し、各語の posting を range-scan して文書ごとに累積 (df はスキャン中に計数、
/// idf は語ごとに適用)、top-k を放出する。スコアは意図的に非公開 (KNN と同じ方針)。
/// 可視性は二次インデックス seek 経路 (<see cref="IndexValueResolver"/>) と同じ世代照合を用い、
/// slot 再利用された false-positive posting を除外する。N/avgdl は DSL に GraphStats があれば
/// <see cref="Bm25CorpusStats"/> から、なければ norms インデックスから近似する。
/// k1=1.2 / b=0.75 (設計既定値)。
/// </remarks>
internal sealed class FullTextScanOperator : IPhysicalOperator
{
    private readonly string _indexName;
    private readonly string _queryText;
    private readonly int _k;
    private readonly Bm25CorpusStats? _corpus;
    private VertexId[] _results = Array.Empty<VertexId>();
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

    public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx)
    {
        if (tx.FullTextSegments is null
            || !tx.FullTextSegments.TryOpen(tx, _indexName, out var ft))
            throw new ConstraintException($"Full-text index '{_indexName}' does not exist.");
        var tokenizer = ft.Tokenizer;

        var (n, avgdl) = Bm25Scorer.ResolveCorpus(ft, _corpus);

        var vertices = tx.Vertices;
        var termStats = _corpus?.Terms;
        List<long>? ranked;

        if (FtsQueryParser.ContainsBooleanOps(_queryText))
        {
            var parsed = FtsQueryParser.ParseBooleanAndExpand(_queryText, tokenizer, ft);
            ranked = Bm25Scorer.RankBoolean(ft, parsed, n, avgdl, candidateSequences: null, termStats);
        }
        else if (FtsQueryParser.ContainsWildcard(_queryText) || FtsQueryParser.ContainsFuzzy(_queryText))
        {
            var terms = FtsQueryParser.ParseAndExpand(_queryText, tokenizer, ft);
            ranked = termStats is not null
                ? Bm25Scorer.RankWandTerms(ft, terms, n, avgdl, termStats, _k,
                    isLive: packed => ft.IsVisibleVertexCandidate(packed, tx))
                : null;
            ranked ??= Bm25Scorer.RankTerms(ft, terms, n, avgdl, candidateSequences: null, termStats);
        }
        else
        {
            ranked = termStats is not null
                ? Bm25Scorer.RankWand(ft, tokenizer, _queryText, n, avgdl, termStats, _k,
                    isLive: packed => ft.IsVisibleVertexCandidate(packed, tx))
                : null;
            ranked ??= Bm25Scorer.Rank(ft, tokenizer, _queryText, n, avgdl, candidateSequences: null, termStats);
        }

        _results = IndexValueResolver.ResolveLiveVertexIds(
                ranked.Where(packed => ft.IsVisibleVertexCandidate(packed, tx)),
                vertices)
            .Take(_k)
            .ToArray();
        _pos = -1;
    }

    public bool MoveNext()
    {
        if (_pos + 1 >= _results.Length) return false;
        _pos++;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.VertexId, LongValue = _results[_pos].Value };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose() { }
}
