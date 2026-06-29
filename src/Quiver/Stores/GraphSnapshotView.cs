using System.Buffers;
using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// トランザクションのリレーションシップストアから
/// CSR (outgoing) と CSC (incoming) 配列をマテリアライズする <see cref="IGraphSnapshotView"/> の既定実装。
/// 同一スナップショットを多数のトラバーサルで使い回すアルゴリズム向けに設計され、1 回の O(N+E) 構築を
/// 多数の走査で償却する。
///
/// <see cref="Build"/> の処理: 生存中の <c>(src, tgt, relId)</c> をすべて 2 パスでスナップショット化 —
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
    private long[] _outRelIds;
    private long[]? _outWeightBits;

    private long[] _inOffsets;
    private long[] _inNeighbors;
    private long[] _inRelIds;

    private bool _disposed;

    public long Epoch { get; }
    public long NodeCount { get; }
    public long EdgeCount { get; }
    public bool HasWeights => _outWeightBits != null;

    private GraphSnapshotView(
        long epoch, long nodeCount, long edgeCount,
        long[] outOffsets, long[] outNeighbors, long[] outRelIds, long[]? outWeights,
        long[] inOffsets, long[] inNeighbors, long[] inRelIds)
    {
        Epoch = epoch;
        NodeCount = nodeCount;
        EdgeCount = edgeCount;
        _outOffsets = outOffsets;
        _outNeighbors = outNeighbors;
        _outRelIds = outRelIds;
        _outWeightBits = outWeights;
        _inOffsets = inOffsets;
        _inNeighbors = inNeighbors;
        _inRelIds = inRelIds;
    }

    /// <summary>
    /// 指定ストアからスナップショットを構築する。本クラスは Quiver.Storage.Records に置くことで、
    /// スナップショットが上位のトランザクション層に依存しないようにしている。
    /// Quiver.csproj 側のファサード <see cref="GraphDatabase.OpenSnapshotView"/> が、
    /// 新規に開いたスナップショットトランザクションのコンポーネントを渡す。
    ///
    /// スナップショットはポイントインタイムで、構築後に渡されたストアへの参照は保持しない。
    /// <paramref name="adj"/> がある場合は渡すことで隣接エポックと inline payload lane (V2 weight) を
    /// 取り込める。隣接ブロック無しでビルドする場合は null を渡す。
    /// </summary>
    public static GraphSnapshotView Build(
        INodeStore nodes,
        IRelationshipStore rels,
        IAdjacencyBlockStore? adj)
    {
        long epoch = adj?.Epoch ?? 0;
        var payloadView = adj as IAdjacencyPayloadView;

        // パス 1: 全ライブ rel をスナップショットし、見つかった最大ノード ID を求める。
        var relList = new List<(long Src, long Tgt, long RelId)>();
        long maxNodeIdSeen = -1;
        foreach (var relId in rels.Scan())
        {
            var r = rels.Read(relId);
            // CSR の node/rel index は Sequence (packed Value ではない)。
            long src = r.Source.Sequence;
            long tgt = r.Target.Sequence;
            relList.Add((src, tgt, relId.Sequence));
            if (src > maxNodeIdSeen) maxNodeIdSeen = src;
            if (tgt > maxNodeIdSeen) maxNodeIdSeen = tgt;
        }

        // ノードストアの HWM も含め、孤立ノード (エッジ無し) もアドレス可能にする。
        long nodeHwm = 0;
        foreach (var nid in nodes.Scan())
        {
            if (nid.Sequence + 1 > nodeHwm) nodeHwm = nid.Sequence + 1;
        }
        long nodeCount = Math.Max(nodeHwm, maxNodeIdSeen + 1);

        long edgeCount = relList.Count;

        // レンタル配列を確保する。offset は NodeCount+1 エントリが必要。+1 の sentinel
        // により OutNeighbors がループ外で境界チェック無しに offsets[i+1] - offsets[i] を使える。
        // Pool は要求より大きい配列を返す場合がある — 論理長は NodeCount / EdgeCount で追跡する。
        long[] outOffsets = ArrayPool<long>.Shared.Rent((int)(nodeCount + 1));
        long[] inOffsets = ArrayPool<long>.Shared.Rent((int)(nodeCount + 1));
        long[] outNeighbors = ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount));
        long[] outRelIds = ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount));
        long[] inNeighbors = ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount));
        long[] inRelIds = ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount));
        long[]? outWeights = payloadView != null
            ? ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount))
            : null;

        // offset をゼロクリアする (レンタル配列は古い内容を持つ可能性がある)。
        Array.Clear(outOffsets, 0, (int)(nodeCount + 1));
        Array.Clear(inOffsets, 0, (int)(nodeCount + 1));

        // 次数を計数する。offsets[i+1] にカウントを格納し prefix-sum を取ることで
        // offsets[i] が行の開始位置になる。これにより別途の次数配列を回避する。
        foreach (var (src, tgt, _) in relList)
        {
            outOffsets[src + 1]++;
            inOffsets[tgt + 1]++;
        }
        for (long i = 1; i <= nodeCount; i++)
        {
            outOffsets[i] += outOffsets[i - 1];
            inOffsets[i] += inOffsets[i - 1];
        }

        // カーソル配列で行ごとの次の空きスロットを記憶しエッジを配置する。
        // カーソル用に別のレンタルバッファを借用し、return 前に返却する。
        long[] outCursor = ArrayPool<long>.Shared.Rent((int)nodeCount);
        long[] inCursor = ArrayPool<long>.Shared.Rent((int)nodeCount);
        try
        {
            Array.Clear(outCursor, 0, (int)nodeCount);
            Array.Clear(inCursor, 0, (int)nodeCount);

            // payload ビューがある場合、V2 ストアをノード毎に走査して relId → weight マップを
            // 事前構築する。ビルド時に一度だけ O(N + E)。
            Dictionary<long, long>? weightByRel = null;
            if (payloadView != null && adj != null)
            {
                weightByRel = new Dictionary<long, long>(relList.Count);
                for (long n = 0; n < nodeCount; n++)
                {
                    if (!adj.HasBlock(new NodeId(n))) continue;
                    using var c = adj.OpenCursor(new NodeId(n), Direction.Outgoing, null);
                    while (c.MoveNext())
                    {
                        if (!adj.IsTombstoned(c.Relationship))
                            weightByRel[c.Relationship.Sequence] = c.WeightRaw; // rel key は Sequence
                    }
                }
            }

            foreach (var (src, tgt, rid) in relList)
            {
                long oSlot = outOffsets[src] + outCursor[src]++;
                outNeighbors[oSlot] = tgt;
                outRelIds[oSlot] = rid;
                if (outWeights != null)
                    outWeights[oSlot] = weightByRel != null && weightByRel.TryGetValue(rid, out var w) ? w : 0;

                long iSlot = inOffsets[tgt] + inCursor[tgt]++;
                inNeighbors[iSlot] = src;
                inRelIds[iSlot] = rid;
            }
        }
        finally
        {
            ArrayPool<long>.Shared.Return(outCursor);
            ArrayPool<long>.Shared.Return(inCursor);
        }

        return new GraphSnapshotView(
            epoch, nodeCount, edgeCount,
            outOffsets, outNeighbors, outRelIds, outWeights,
            inOffsets, inNeighbors, inRelIds);
    }

    public int OutDegree(NodeId nodeId)
    {
        long n = nodeId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)NodeCount) return 0;
        return (int)(_outOffsets[n + 1] - _outOffsets[n]);
    }

    public int InDegree(NodeId nodeId)
    {
        long n = nodeId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)NodeCount) return 0;
        return (int)(_inOffsets[n + 1] - _inOffsets[n]);
    }

    public ReadOnlySpan<long> OutNeighbors(NodeId nodeId)
    {
        long n = nodeId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)NodeCount) return ReadOnlySpan<long>.Empty;
        long start = _outOffsets[n];
        long len = _outOffsets[n + 1] - start;
        return _outNeighbors.AsSpan((int)start, (int)len);
    }

    public ReadOnlySpan<long> InNeighbors(NodeId nodeId)
    {
        long n = nodeId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)NodeCount) return ReadOnlySpan<long>.Empty;
        long start = _inOffsets[n];
        long len = _inOffsets[n + 1] - start;
        return _inNeighbors.AsSpan((int)start, (int)len);
    }

    public ReadOnlySpan<long> OutRelationshipIds(NodeId nodeId)
    {
        long n = nodeId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)NodeCount) return ReadOnlySpan<long>.Empty;
        long start = _outOffsets[n];
        long len = _outOffsets[n + 1] - start;
        return _outRelIds.AsSpan((int)start, (int)len);
    }

    public ReadOnlySpan<long> InRelationshipIds(NodeId nodeId)
    {
        long n = nodeId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)NodeCount) return ReadOnlySpan<long>.Empty;
        long start = _inOffsets[n];
        long len = _inOffsets[n + 1] - start;
        return _inRelIds.AsSpan((int)start, (int)len);
    }

    public ReadOnlySpan<long> WeightBitsOut(NodeId nodeId)
    {
        if (_outWeightBits == null) return ReadOnlySpan<long>.Empty;
        long n = nodeId.Sequence; // CSR index は Sequence
        if ((ulong)n >= (ulong)NodeCount) return ReadOnlySpan<long>.Empty;
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
        ArrayPool<long>.Shared.Return(_outRelIds);
        if (_outWeightBits != null) ArrayPool<long>.Shared.Return(_outWeightBits);
        ArrayPool<long>.Shared.Return(_inOffsets);
        ArrayPool<long>.Shared.Return(_inNeighbors);
        ArrayPool<long>.Shared.Return(_inRelIds);
        _outOffsets = null!;
        _outNeighbors = null!;
        _outRelIds = null!;
        _outWeightBits = null;
        _inOffsets = null!;
        _inNeighbors = null!;
        _inRelIds = null!;
    }
}
