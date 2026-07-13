using System.Buffers;
using System.Runtime.InteropServices;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// graph-first ダイアディックスコアリング演算子。上流ソースを候補リストに排出し、
/// <see cref="IGraphAccessMethods.TryGetVector"/> でベクトルをチャンク単位に収集した後、
/// DSL 構築時にキャプチャした <see cref="DyadicScoreFunc"/> で各候補を <c>b</c> に対しスコアリングする。
/// gather と score を 2 フェーズに分離し、ユーザ提供の演算子コードがストアロック保持中に走らないようにする。
/// <para>
/// <see cref="_oversample"/> が設定済みかつインデックスが <see cref="VectorIndexKind.HnswFlat"/> の場合、
/// HNSW がインデックス組み込みメトリクスで <c>k × oversample</c> 件を事前フィルタし、
/// カスタム演算子がその候補のみを再ランクする 2 段パイプラインを使用する。
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
            if (slot.Type != TupleSlotType.NodeId)
                continue;

            // EntityCandidateSet は raw Sequence を受ける互換 adapter。
            // logical pipeline の full ID は primary Read で検証してから、その直後にだけ
            // physical vector key へ落とす。stale full ID は新 slot 所有者へ retarget しない。
            using var node = tx.Nodes.Read(new NodeId(slot.LongValue));
            if (node.InUse)
                candidateIds.Add(node.Id.Sequence);
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
    /// HNSW oversample → カスタム rerank 経路。インデックス組み込みメトリクスで
    /// <see cref="IGraphAccessMethods.KnnSearchFiltered"/> により候補を絞り込み、
    /// 絞り込み後の候補のみをカスタム演算子で再スコアリングする。
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
    /// ベクトルをチャンク単位に収集しカスタム演算子でスコアリングする
    /// (brute-force / oversample 両経路で共用)。
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

                // Phase 1: gather — TryGetVector 呼び出しごとにストアロックを短時間取得する
                for (int i = pos; i < end; i++)
                {
                    var dest = gatherBuf.AsSpan(gathered * dim, dim);
                    if (tx.Access.TryGetVector(EntityKind.Node, candidateIds[i], _indexName, dest))
                    {
                        gatherIds[gathered] = candidateIds[i];
                        gathered++;
                    }
                }

                // Phase 2: score — ユーザ演算子コードを実行 (ストアロック非保持)
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
