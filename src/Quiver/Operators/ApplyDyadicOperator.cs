using System.Buffers;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// Graph-first brute-force dyadic scoring operator. Drains the upstream source
/// into a candidate list, gathers stored vectors in chunks via
/// <see cref="IGraphAccessMethods.TryGetVector"/>, then scores each against <c>b</c>
/// using a <see cref="DyadicScoreFunc"/> delegate captured at DSL build time.
/// Gather and score are separated into two phases so that user-supplied operator
/// code never runs while a store lock is held.
/// </summary>
internal sealed class ApplyDyadicOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceNodeColumn;
    private readonly string _indexName;
    private readonly float[] _bVector;
    private readonly Range[]? _regions;
    private readonly int _k;
    private readonly DyadicScoreFunc _scorer;
    private readonly Type _operatorType;

    private VectorSearchResult[]? _results;
    private int _resultIndex = -1;
    private readonly TupleSlot[] _buf = new TupleSlot[1];

    private const int ChunkSize = 256;

    public ApplyDyadicOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        string indexName,
        float[] bVector,
        Range[]? regions,
        int k,
        DyadicScoreFunc scorer,
        Type operatorType)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sourceNodeColumn = sourceNodeColumn;
        _indexName = indexName;
        _bVector = bVector;
        _regions = regions;
        _k = k;
        _scorer = scorer;
        _operatorType = operatorType;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buf);

    public void Open(ITransaction tx)
    {
        _source.Open(tx);

        var candidateIds = new List<long>();
        while (_source.MoveNext())
        {
            var slot = _source.Current[_sourceNodeColumn];
            if (slot.Type == TupleSlotType.NodeId) candidateIds.Add(slot.LongValue);
        }

        if (candidateIds.Count == 0)
        {
            _results = [];
            return;
        }

        if (!tx.Access.TryGetVectorIndexSpec(_indexName, out var spec))
            throw new VectorException($"Vector index '{_indexName}' does not exist.");

        int dim = spec.Dimensions;
        ReadOnlySpan<Range> regionSpan = _regions.AsSpan();
        ReadOnlySpan<float> bSpan = _bVector;

        var heap = new VectorKnnHeap(_k);
        int total = candidateIds.Count;
        int chunk = Math.Min(ChunkSize, total);

        var gatherBuf = ArrayPool<float>.Shared.Rent(chunk * dim);
        var gatherIds = ArrayPool<long>.Shared.Rent(chunk);
        try
        {
            int pos = 0;
            while (pos < total)
            {
                int end = Math.Min(pos + chunk, total);
                int gathered = 0;

                // Phase 1: gather — each TryGetVector call briefly acquires the store lock
                for (int i = pos; i < end; i++)
                {
                    var dest = gatherBuf.AsSpan(gathered * dim, dim);
                    if (tx.Access.TryGetVector(EntityKind.Node, candidateIds[i], _indexName, dest))
                    {
                        gatherIds[gathered] = candidateIds[i];
                        gathered++;
                    }
                }

                // Phase 2: score — user operator code, no store lock held
                for (int i = 0; i < gathered; i++)
                {
                    ReadOnlySpan<float> aSpan = gatherBuf.AsSpan(i * dim, dim);
                    float score = _scorer(aSpan, bSpan, regionSpan);
                    if (float.IsNaN(score))
                        throw new VectorException(
                            $"Operator {_operatorType.Name} returned NaN for entity {gatherIds[i]}.");
                    heap.Offer(new VectorSearchResult(EntityKind.Node, gatherIds[i], score));
                }

                pos = end;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(gatherBuf);
            ArrayPool<long>.Shared.Return(gatherIds);
        }

        _results = heap.ToSortedArray();
    }

    public bool MoveNext()
    {
        if (++_resultIndex >= _results!.Length) return false;
        var hit = _results[_resultIndex];
        _buf[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = hit.EntityId };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose() => _source.Dispose();
}
