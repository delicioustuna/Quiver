using System.Buffers;
using Quiver.Core;

namespace Quiver.Stores;

/// <summary>
/// PW-15 / codex_advice_3 §7.7. Default <see cref="IGraphSnapshotView"/>
/// implementation that materialises CSR (outgoing) and CSC (incoming) arrays
/// from a transaction's relationship store. Designed for repeated multi-pass
/// algorithms — one O(N+E) build amortised over many traversals.
///
/// Built by <see cref="Build"/>: snapshot every live <c>(src, tgt, relId)</c>
/// in two passes — one to size the rows and one to place edges. Arrays are
/// rented from <see cref="ArrayPool{T}.Shared"/> so dispose returns them.
///
/// Weight lane is populated when the source store implements
/// <see cref="IAdjacencyPayloadView"/>; otherwise <see cref="HasWeights"/>
/// is false and <see cref="WeightBitsOut"/> returns empty.
/// </summary>
public sealed class GraphSnapshotView : IGraphSnapshotView
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
    /// Build a snapshot from the given stores. Lives in Quiver.Stores so the
    /// snapshot does not depend on the higher-level transaction layer; the
    /// <see cref="GraphDatabase.OpenSnapshotView"/> facade in Quiver.csproj
    /// passes the components from a freshly opened snapshot transaction.
    ///
    /// The snapshot is point-in-time and does not retain references to the
    /// passed stores after construction. Pass <paramref name="adj"/> when
    /// available so the snapshot can pick up the adjacency epoch and any
    /// inline payload lane (V2 weight); pass null for adjacency-less builds.
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
