using Quiver.Core;

namespace Quiver.Query.Physical;

/// <summary>
/// Membership set of <see cref="NodeId"/>s used as the probe side of
/// <see cref="RelationshipScanExpandOperator"/> (PW-17). Two backing strategies:
/// a dense bitmap when ids cluster near 0, or a <see cref="HashSet{T}"/> when
/// the id space is sparse. The chosen strategy is opaque to callers.
/// </summary>
public sealed class FrontierSet
{
    private readonly NodeBitSet? _bits;
    private readonly HashSet<long>? _hash;

    private FrontierSet(NodeBitSet bits) { _bits = bits; _hash = null; }
    private FrontierSet(HashSet<long> hash) { _bits = null; _hash = hash; }

    /// <summary>Number of distinct nodes in the set.</summary>
    public int Count => _bits?.Count ?? _hash!.Count;

    /// <summary>True when <paramref name="nodeId"/> was added to the set.</summary>
    public bool Contains(NodeId nodeId)
        => _bits?.Contains(nodeId.Value) ?? _hash!.Contains(nodeId.Value);

    /// <summary>Build a frontier from <paramref name="nodes"/>. Heuristic picks bitset vs hashset.</summary>
    public static FrontierSet Build(IEnumerable<NodeId> nodes)
    {
        // First pass into a list to learn min/max/count cheaply.
        var buf = new List<long>(64);
        long max = -1;
        foreach (var n in nodes)
        {
            buf.Add(n.Value);
            if (n.Value > max) max = n.Value;
        }
        return Build(buf, max);
    }

    internal static FrontierSet Build(List<long> ids, long maxId)
    {
        // Bitmap is cheaper when max id stays under ~32 * count. That keeps the
        // bitmap to roughly count * 4 bytes — comparable to a HashSet's load.
        if (maxId >= 0 && maxId <= 32L * Math.Max(ids.Count, 1) && maxId < int.MaxValue)
        {
            var bits = new NodeBitSet((int)maxId + 1);
            foreach (var v in ids) bits.Add(v);
            return new FrontierSet(bits);
        }
        var hash = new HashSet<long>(ids.Count);
        foreach (var v in ids) hash.Add(v);
        return new FrontierSet(hash);
    }
}

/// <summary>
/// Dense bitmap over <c>[0, Capacity)</c>. Backs <see cref="FrontierSet"/> in the
/// dense-id case. Allocates <c>(Capacity + 63) / 64</c> ulongs.
/// </summary>
public sealed class NodeBitSet
{
    private readonly ulong[] _words;
    private int _count;

    public int Capacity { get; }
    public int Count => _count;

    public NodeBitSet(int capacity)
    {
        Capacity = capacity;
        _words = new ulong[(capacity + 63) >> 6];
    }

    public void Add(long id)
    {
        if ((ulong)id >= (ulong)Capacity) return;
        int w = (int)(id >> 6);
        ulong mask = 1UL << (int)(id & 63);
        if ((_words[w] & mask) == 0)
        {
            _words[w] |= mask;
            _count++;
        }
    }

    public bool Contains(long id)
    {
        if ((ulong)id >= (ulong)Capacity) return false;
        int w = (int)(id >> 6);
        ulong mask = 1UL << (int)(id & 63);
        return (_words[w] & mask) != 0;
    }
}
