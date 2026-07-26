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
/// <see cref="_oversample"/> が設定済みの場合、
/// HNSW がインデックス組み込みメトリクスで <c>k × oversample</c> 件を事前フィルタし、
/// カスタム演算子がその候補のみを再ランクする 2 段パイプラインを使用する。
/// </para>
/// </summary>
internal sealed class ApplyDyadicOperator : IPhysicalOperator
{
    private readonly IPhysicalOperator _source;
    private readonly int _sourceVertexColumn;
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
        int sourceVertexColumn,
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
        _sourceVertexColumn = sourceVertexColumn;
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

    public TupleSchema Schema { get; } = new([new ColumnDefinition("vertexId", TupleSlotType.VertexId)]);
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

        var candidateIds = new List<EntityRef>();
        while (_source.MoveNext())
        {
            var slot = _source.Current[_sourceVertexColumn];
            if (slot.Type != TupleSlotType.VertexId)
                continue;

            using var vertex = tx.Vertices.Read(new VertexId(slot.LongValue));
            if (vertex.InUse)
                candidateIds.Add(EntityRef.From(vertex.Id));
        }

        if (candidateIds.Count == 0)
        {
            _results = [];
            return;
        }

        if (!tx.Access.TryGetVectorIndex(_indexName, out var descriptor))
            throw new VectorException($"Vector index '{_indexName}' does not exist.");

        if (_oversample is not null)
        {
            _results = OpenOversample(tx, bVector, candidateIds, descriptor);
            return;
        }

        _results = ScoreCandidates(tx, bVector, candidateIds, descriptor);
    }

    /// <summary>
    /// HNSW oversample → カスタム rerank 経路。インデックス組み込みメトリクスで
    /// <see cref="IGraphAccessMethods.KnnSearchFiltered"/> により候補を絞り込み、
    /// 絞り込み後の候補のみをカスタム演算子で再スコアリングする。
    /// </summary>
    private VectorSearchResult[] OpenOversample(
        ITransaction tx,
        float[] bVector,
        List<EntityRef> upstreamIds,
        VectorIndexDescriptor descriptor)
    {
        int hnswK = checked(_k * _oversample!.Value);
        var candidates = upstreamIds.ToHashSet();

        var narrowedIds = new List<EntityRef>();
        using (var cursor = tx.Access.KnnSearchFiltered(
                   tx,
                   _indexName,
                   bVector,
                   hnswK,
                   candidates))
        {
            while (cursor.MoveNext())
                narrowedIds.Add(cursor.Current.Owner);
        }

        if (narrowedIds.Count == 0) return [];
        return ScoreCandidates(tx, bVector, narrowedIds, descriptor);
    }

    /// <summary>
    /// ベクトルをチャンク単位に収集しカスタム演算子でスコアリングする
    /// (brute-force / oversample 両経路で共用)。
    /// </summary>
    private VectorSearchResult[] ScoreCandidates(
        ITransaction tx,
        float[] bVector,
        List<EntityRef> candidateIds,
        VectorIndexDescriptor descriptor)
    {
        int dim = descriptor.Dimensions;
        ReadOnlySpan<Range> regionSpan = _regions.AsSpan();
        ReadOnlySpan<float> bSpan = bVector;

        var heap = new VectorKnnHeap(_k);
        int total = candidateIds.Count;
        int chunk = Math.Min(ChunkSize, total);

        var gatherBuf = ArrayPool<float>.Shared.Rent(chunk * dim);
        var gatherIds = ArrayPool<EntityRef>.Shared.Rent(chunk);
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
                    if (tx.Access.TryGetVector(
                            tx,
                            candidateIds[i],
                            _indexName,
                            dest))
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
                    heap.Offer(new VectorSearchResult(gatherIds[i], score));
                }

                pos = end;
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(gatherBuf);
            ArrayPool<EntityRef>.Shared.Return(gatherIds);
        }

        return heap.ToSortedArray();
    }

    public bool MoveNext()
    {
        if (++_resultIndex >= _results!.Length) return false;
        var hit = _results[_resultIndex];
        _buf[0] = new TupleSlot
        {
            Type = TupleSlotType.VertexId,
            LongValue = hit.Owner.Value,
        };
        var s = Statistics;
        s.RowsProduced++;
        Statistics = s;
        return true;
    }

    public void Dispose() => _source.Dispose();
}
