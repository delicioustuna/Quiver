using System.Buffers;
using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// PW-15 / codex_advice_3 7.7 節。トランザクションのリレーションシップストアから
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

        // Pass 1: snapshot all live rels and find max node id seen.
        var relList = new List<(long Src, long Tgt, long RelId)>();
        long maxNodeIdSeen = -1;
        foreach (var relId in rels.Scan())
        {
            var r = rels.Read(relId);
            long src = r.Source.Value;
            long tgt = r.Target.Value;
            relList.Add((src, tgt, relId.Value));
            if (src > maxNodeIdSeen) maxNodeIdSeen = src;
            if (tgt > maxNodeIdSeen) maxNodeIdSeen = tgt;
        }

        // Also include node-store hwm so isolated nodes (no edges) are addressable.
        long nodeHwm = 0;
        foreach (var nid in nodes.Scan())
        {
            if (nid.Value + 1 > nodeHwm) nodeHwm = nid.Value + 1;
        }
        long nodeCount = Math.Max(nodeHwm, maxNodeIdSeen + 1);

        long edgeCount = relList.Count;

        // Allocate rented arrays. Offsets need NodeCount+1 entries; +1 sentinel
        // lets OutNeighbors use offsets[i+1] - offsets[i] without bounds check
        // outside the loop. Pool may return arrays larger than requested — we
        // track the logical lengths via NodeCount / EdgeCount.
        long[] outOffsets = ArrayPool<long>.Shared.Rent((int)(nodeCount + 1));
        long[] inOffsets = ArrayPool<long>.Shared.Rent((int)(nodeCount + 1));
        long[] outNeighbors = ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount));
        long[] outRelIds = ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount));
        long[] inNeighbors = ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount));
        long[] inRelIds = ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount));
        long[]? outWeights = payloadView != null
            ? ArrayPool<long>.Shared.Rent((int)Math.Max(1, edgeCount))
            : null;

        // Zero offsets (rented arrays may carry stale content).
        Array.Clear(outOffsets, 0, (int)(nodeCount + 1));
        Array.Clear(inOffsets, 0, (int)(nodeCount + 1));

        // Count degrees. We store counts in offsets[i+1] then prefix-sum so that
        // offsets[i] becomes the row start. This avoids a separate degree array.
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

        // Place edges using cursor arrays to remember the next free slot per row.
        // We borrow another rented buffer for the cursors — released before return.
        long[] outCursor = ArrayPool<long>.Shared.Rent((int)nodeCount);
        long[] inCursor = ArrayPool<long>.Shared.Rent((int)nodeCount);
        try
        {
            Array.Clear(outCursor, 0, (int)nodeCount);
            Array.Clear(inCursor, 0, (int)nodeCount);

            // If we have a payload view, prebuild a relId -> weight map by walking
            // the V2 store node-by-node. This is O(N + E) once at build time.
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
                            weightByRel[c.Relationship.Value] = c.WeightRaw;
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
        long n = nodeId.Value;
        if ((ulong)n >= (ulong)NodeCount) return 0;
        return (int)(_outOffsets[n + 1] - _outOffsets[n]);
    }

    public int InDegree(NodeId nodeId)
    {
        long n = nodeId.Value;
        if ((ulong)n >= (ulong)NodeCount) return 0;
        return (int)(_inOffsets[n + 1] - _inOffsets[n]);
    }

    public ReadOnlySpan<long> OutNeighbors(NodeId nodeId)
    {
        long n = nodeId.Value;
        if ((ulong)n >= (ulong)NodeCount) return ReadOnlySpan<long>.Empty;
        long start = _outOffsets[n];
        long len = _outOffsets[n + 1] - start;
        return _outNeighbors.AsSpan((int)start, (int)len);
    }

    public ReadOnlySpan<long> InNeighbors(NodeId nodeId)
    {
        long n = nodeId.Value;
        if ((ulong)n >= (ulong)NodeCount) return ReadOnlySpan<long>.Empty;
        long start = _inOffsets[n];
        long len = _inOffsets[n + 1] - start;
        return _inNeighbors.AsSpan((int)start, (int)len);
    }

    public ReadOnlySpan<long> OutRelationshipIds(NodeId nodeId)
    {
        long n = nodeId.Value;
        if ((ulong)n >= (ulong)NodeCount) return ReadOnlySpan<long>.Empty;
        long start = _outOffsets[n];
        long len = _outOffsets[n + 1] - start;
        return _outRelIds.AsSpan((int)start, (int)len);
    }

    public ReadOnlySpan<long> InRelationshipIds(NodeId nodeId)
    {
        long n = nodeId.Value;
        if ((ulong)n >= (ulong)NodeCount) return ReadOnlySpan<long>.Empty;
        long start = _inOffsets[n];
        long len = _inOffsets[n + 1] - start;
        return _inRelIds.AsSpan((int)start, (int)len);
    }

    public ReadOnlySpan<long> WeightBitsOut(NodeId nodeId)
    {
        if (_outWeightBits == null) return ReadOnlySpan<long>.Empty;
        long n = nodeId.Value;
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
