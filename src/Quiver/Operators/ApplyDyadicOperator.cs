using System.Buffers;
using System.Runtime.InteropServices;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// Graph-first dyadic scoring operator. Drains the upstream source into a candidate
/// list, gathers stored vectors in chunks via
/// <see cref="IGraphAccessMethods.TryGetVector"/>, then scores each against <c>b</c>
/// using a <see cref="DyadicScoreFunc"/> delegate captured at DSL build time.
/// Gather and score are separated into two phases so that user-supplied operator
/// code never runs while a store lock is held.
/// <para>
/// When <see cref="_oversample"/> is set and the index is <see cref="VectorIndexKind.HnswFlat"/>,
/// a two-stage pipeline is used: HNSW pre-filters <c>k × oversample</c> candidates
/// using the index's built-in metric, then the custom operator re-ranks only those
/// candidates. This trades exactness for speed on large candidate sets.
/// </para>
/// </summary>
internal sealed class ApplyDyadicOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceNodeColumn;
    private readonly string _indexName;
    private readonly float[]? _bVectorStatic;
    private readonly IPhysicalOperator? _bSource;
    private readonly int _bFloatColumn;
    private readonly Range[]? _regions;
    private readonly int _k;
    private readonly DyadicScoreFunc _scorer;
    private readonly Type _operatorType;
    private readonly int? _oversample;

    private VectorSearchResult[]? _results;
    private int _resultIndex = -1;
    private readonly TupleSlot[] _buf = new TupleSlot[1];

    private const int ChunkSize = 256;

    public ApplyDyadicOperator(
        IPhysicalOperator source,
        int sourceNodeColumn,
        string indexName,
        float[]? bVectorStatic,
        IPhysicalOperator? bSource,
        int bFloatColumn,
        Range[]? regions,
        int k,
        DyadicScoreFunc scorer,
        Type operatorType,
        int? oversample = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sourceNodeColumn = sourceNodeColumn;
        _indexName = indexName;
        _bVectorStatic = bVectorStatic;
        _bSource = bSource;
        _bFloatColumn = bFloatColumn;
        _regions = regions;
        _k = k;
        _scorer = scorer;
        _operatorType = operatorType;
        _oversample = oversample;
    }

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics { get; private set; }
    public TupleRef Current => new(_buf);

    public void Open(ITransaction tx)
    {
        float[] bVector;
        if (_bSource != null)
        {
            _bSource.Open(tx);
            if (!_bSource.MoveNext())
                throw new VectorException("Traversal 'b' produced no results.");
            var bytesSpan = _bSource.GetBytes(_bFloatColumn);
            bVector = MemoryMarshal.Cast<byte, float>(bytesSpan).ToArray();
            _bSource.Dispose();
        }
        else
        {
            bVector = _bVectorStatic!;
        }

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

        if (_oversample is not null)
        {
            _results = OpenOversample(tx, bVector, candidateIds, spec);
            return;
        }

        _results = ScoreCandidates(tx, bVector, candidateIds, spec);
    }

    /// <summary>
    /// HNSW oversample → custom rerank path. Narrows the candidate set via
    /// <see cref="IGraphAccessMethods.KnnSearchFiltered"/> using the index's
    /// built-in metric, then re-scores only the narrowed candidates with the
    /// custom operator.
    /// </summary>
    private VectorSearchResult[] OpenOversample(
        ITransaction tx,
        float[] bVector,
        List<long> upstreamIds,
        VectorIndexSpec spec)
    {
        if (spec.IndexKind == VectorIndexKind.FlatOnly)
            throw new VectorException(
                $"Vector index '{_indexName}' is FlatOnly and does not support HNSW oversample. " +
                "Remove the oversample parameter or use a HnswFlat index.");

        int hnswK = checked(_k * _oversample!.Value);
        var candidates = new EntityCandidateSet(EntityKind.Node, upstreamIds);

        var narrowedIds = new List<long>();
        using (var cursor = tx.Access.KnnSearchFiltered(_indexName, bVector, hnswK, candidates))
        {
            while (cursor.MoveNext())
                narrowedIds.Add(cursor.Current.EntityId);
        }

        if (narrowedIds.Count == 0) return [];
        return ScoreCandidates(tx, bVector, narrowedIds, spec);
    }

    /// <summary>
    /// Gather vectors in chunks and score with the custom operator (shared by both
    /// brute-force and oversample paths).
    /// </summary>
    private VectorSearchResult[] ScoreCandidates(
        ITransaction tx,
        float[] bVector,
        List<long> candidateIds,
        VectorIndexSpec spec)
    {
        int dim = spec.Dimensions;
        ReadOnlySpan<Range> regionSpan = _regions.AsSpan();
        ReadOnlySpan<float> bSpan = bVector;

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

        return heap.ToSortedArray();
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
