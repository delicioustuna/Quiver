using System.Buffers;
using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// トランザクションのEdgeストアから
/// CSR (outgoing) と CSC (incoming) 配列をマテリアライズする <see cref="IGraphSnapshotView"/> の既定実装。
/// 同一スナップショットを多数のトラバーサルで使い回すアルゴリズム向けに設計され、1 回の O(N+E) 構築を
/// 多数の走査で償却する。
///
/// <see cref="Build"/> の処理: 生存中の <c>(src, tgt, edgeId)</c> をすべて 2 パスでスナップショット化 —
/// 1 パス目で行サイズを決め、2 パス目でエッジを配置する。配列は
/// <see cref="ArrayPool{T}.Shared"/> からレンタルし、Dispose で返却する。
///
/// ソースストアが <see cref="IAdjacencyPayloadView"/> を実装している場合のみ weight lane が埋まる。
/// それ以外では <see cref="HasWeights"/> は false で <see cref="WeightBitsOut"/> は空を返す。
/// </summary>
internal sealed class GraphSnapshotView : IGraphSnapshotView
{
    private long[] _outOffsets;
    private long[] _outNeighbors;
    private long[] _outEdgeIds;
    private long[]? _outWeightBits;

    private long[] _inOffsets;
    private long[] _inNeighbors;
    private long[] _inEdgeIds;

    private bool _disposed;

    public long Epoch { get; }
    public long VertexCount { get; }
    public long EdgeCount { get; }
    public bool HasWeights => _outWeightBits != null;

    private GraphSnapshotView(
        long epoch, long vertexCount, long edgeCount,
        long[] outOffsets, long[] outNeighbors, long[] outEdgeIds, long[]? outWeights,
        long[] inOffsets, long[] inNeighbors, long[] inEdgeIds)
    {
        Epoch = epoch;
        VertexCount = vertexCount;
        EdgeCount = edgeCount;
        _outOffsets = outOffsets;
        _outNeighbors = outNeighbors;
        _outEdgeIds = outEdgeIds;
        _outWeightBits = outWeights;
        _inOffsets = inOffsets;
        _inNeighbors = inNeighbors;
        _inEdgeIds = inEdgeIds;
    }

    /// <summary>
    /// 指定ストアからスナップショットを構築する。本クラスは Quiver.Storage.Records に置くことで、
    /// スナップショットが上位のトランザクション層に依存しないようにしている。
    /// Quiver.csproj 側のファサード <see cref="QuiverDatabase.OpenSnapshotView"/> が、
    /// 新規に開いたスナップショットトランザクションのコンポーネントを渡す。
    ///
    /// スナップショットはポイントインタイムで、構築後に渡されたストアへの参照は保持しない。
    /// <paramref name="adj"/> がある場合は渡すことで隣接エポックと inline payload lane (V2 weight) を
    /// 取り込める。隣接ブロック無しでビルドする場合は null を渡す。
    /// </summary>
    public static GraphSnapshotView Build(
        IVertexStore vertices,
        IEdgeStore edges,
        IAdjacencyBlockStore? adj)
    {
        long epoch = adj?.Epoch ?? 0;
        var payloadView = adj as IAdjacencyPayloadView;

        // パス 1: 全ライブ edge をスナップショットし、見つかった最大Vertex ID を求める。
        var edgeList = new List<(long Src, long Tgt, long EdgeId)>();
        long maxVertexIdSeen = -1;
        foreach (var edgeId in edges.Scan())
        {
            var r = edges.Read(edgeId);
            // CSR の vertex/edge index は Sequence (packed Value ではない)。
            long src = r.Source.Sequence;
            long tgt = r.Target.Sequence;
            edgeList.Add((src, tgt, edgeId.Sequence));
            if (src > maxVertexIdSeen) maxVertexIdSeen = src;
            if (tgt > maxVertexIdSeen) maxVertexIdSeen = tgt;
        }

        // Vertexストアの HWM も含め、孤立Vertex (エッジ無し) もアドレス可能にする。
        long vertexHwm = 0;
        foreach (var nid in vertices.Scan())
        {
            if (nid.Sequence + 1 > vertexHwm) vertexHwm = nid.Sequence + 1;
        }
        long vertexCount = Math.Max(vertexHwm, maxVertexIdSeen + 1);

        long edgeCount = edgeList.Count;

        // レンタル配列を確保する。offset は VertexCount+1 エントリが必要。+1 の sentinel
        // により OutNeighbors がループ外で境界チェック無しに offsets[i+1] - offsets[i] を使える。
        // Pool は要求より大きい配列を返す場合がある — 論理長は VertexCount / EdgeCount で追跡する。
        long[] outOffsets = ArrayPool<long>.Shared.Rent((int)(vertexCount + 1));
        long[] inOffsets = ArrayPool<long>.Shared.Rent((int)(vertexCount + 1));
        long[] outNeighbors = ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount));
        long[] outEdgeIds = ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount));
        long[] inNeighbors = ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount));
        long[] inEdgeIds = ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount));
        long[]? outWeights = payloadView != null
            ? ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount))
            : null;

        // offset をゼロクリアする (レンタル配列は古い内容を持つ可能性がある)。
        Array.Clear(outOffsets, 0, (int)(vertexCount + 1));
        Array.Clear(inOffsets, 0, (int)(vertexCount + 1));

        // 次数を計数する。offsets[i+1] にカウントを格納し prefix-sum を取ることで
        // offsets[i] が行の開始位置になる。これにより別途の次数配列を回避する。
        foreach (var (src, tgt, _) in edgeList)
        {
            outOffsets[src + 1]++;
            inOffsets[tgt + 1]++;
        }
        for (long i = 1; i <= vertexCount; i++)
        {
            outOffsets[i] += outOffsets[i - 1];
            inOffsets[i] += inOffsets[i - 1];
        }

        // カーソル配列で行ごとの次の空きスロットを記憶しエッジを配置する。
        // カーソル用に別のレンタルバッファを借用し、return 前に返却する。
        long[] outCursor = ArrayPool<long>.Shared.Rent((int)vertexCount);
        long[] inCursor = ArrayPool<long>.Shared.Rent((int)vertexCount);
        try
        {
            Array.Clear(outCursor, 0, (int)vertexCount);
            Array.Clear(inCursor, 0, (int)vertexCount);

            // payload ビューがある場合、V2 ストアをVertex毎に走査して edgeId → weight マップを
            // 事前構築する。ビルド時に一度だけ O(N + E)。
            Dictionary<long, long>? weightByEdge = null;
            if (payloadView != null && adj != null)
            {
                weightByEdge = new Dictionary<long, long>(edgeList.Count);
                for (long n = 0; n < vertexCount; n++)
                {
                    if (!adj.HasBlock(new VertexId(n))) continue;
                    using var c = adj.OpenCursor(new VertexId(n), Direction.Outgoing, null);
                    while (c.MoveNext())
                    {
                        if (!adj.IsTombstoned(c.Edge))
                            weightByEdge[c.Edge.Sequence] = c.WeightRaw; // edge key は Sequence
                    }
                }
            }

            foreach (var (src, tgt, rid) in edgeList)
            {
                long oSlot = outOffsets[src] + outCursor[src]++;
                outNeighbors[oSlot] = tgt;
                outEdgeIds[oSlot] = rid;
                if (outWeights != null)
                    outWeights[oSlot] = weightByEdge != null && weightByEdge.TryGetValue(rid, out var w) ? w : 0;

                long iSlot = inOffsets[tgt] + inCursor[tgt]++;
                inNeighbors[iSlot] = src;
                inEdgeIds[iSlot] = rid;
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(outCursor);
            ArrayPool<long>.Shared.Return(inCursor);
        }

        return new GraphSnapshotView(
            epoch, vertexCount, edgeCount,
            outOffsets, outNeighbors, outEdgeIds, outWeights,
            inOffsets, inNeighbors, inEdgeIds);
    }

    public int OutDegree(VertexId vertexId)
    {
        long n = vertexId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)VertexCount) return 0;
        return (int)(_outOffsets[n + 1] - _outOffsets[n]);
    }

    public int InDegree(VertexId vertexId)
    {
        long n = vertexId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)VertexCount) return 0;
        return (int)(_inOffsets[n + 1] - _inOffsets[n]);
    }

    public ReadOnlySpan<long> OutNeighbors(VertexId vertexId)
    {
        long n = vertexId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)VertexCount) return ReadOnlySpan<long>.Empty;
        long start = _outOffsets[n];
        long len = _outOffsets[n + 1] - start;
        return _outNeighbors.AsSpan((int)start, (int)len);
    }

    public ReadOnlySpan<long> InNeighbors(VertexId vertexId)
    {
        long n = vertexId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)VertexCount) return ReadOnlySpan<long>.Empty;
        long start = _inOffsets[n];
        long len = _inOffsets[n + 1] - start;
        return _inNeighbors.AsSpan((int)start, (int)len);
    }

    public ReadOnlySpan<long> OutEdgeIds(VertexId vertexId)
    {
        long n = vertexId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)VertexCount) return ReadOnlySpan<long>.Empty;
        long start = _outOffsets[n];
        long len = _outOffsets[n + 1] - start;
        return _outEdgeIds.AsSpan((int)start, (int)len);
    }

    public ReadOnlySpan<long> InEdgeIds(VertexId vertexId)
    {
        long n = vertexId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)VertexCount) return ReadOnlySpan<long>.Empty;
        long start = _inOffsets[n];
        long len = _inOffsets[n + 1] - start;
        return _inEdgeIds.AsSpan((int)start, (int)len);
    }

    public ReadOnlySpan<long> WeightBitsOut(VertexId vertexId)
    {
        if (_outWeightBits == null) return ReadOnlySpan<long>.Empty;
        long n = vertexId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)VertexCount) return ReadOnlySpan<long>.Empty;
        long start = _outOffsets[n];
        long len = _outOffsets[n + 1] - start;
        return _outWeightBits.AsSpan((int)start, (int)len);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<long>.Shared.Return(_outOffsets);
        ArrayPool<long>.Shared.Return(_outNeighbors);
        ArrayPool<long>.Shared.Return(_outEdgeIds);
        if (_outWeightBits != null) ArrayPool<long>.Shared.Return(_outWeightBits);
        ArrayPool<long>.Shared.Return(_inOffsets);
        ArrayPool<long>.Shared.Return(_inNeighbors);
        ArrayPool<long>.Shared.Return(_inEdgeIds);
        _outOffsets = null!;
        _outNeighbors = null!;
        _outEdgeIds = null!;
        _outWeightBits = null;
        _inOffsets = null!;
        _inNeighbors = null!;
        _inEdgeIds = null!;
    }
}
