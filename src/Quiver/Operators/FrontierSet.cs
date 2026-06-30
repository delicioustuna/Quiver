using Quiver.Core;

namespace Quiver.Query.Physical;

/// <summary>
/// <see cref="RelationshipScanExpandOperator"/> のプローブ側に使う
/// <see cref="NodeId"/> メンバシップセット。ID が 0 付近に密集する場合は dense bitmap、
/// 疎な場合は <see cref="HashSet{T}"/> の 2 戦略を持ち、呼び出し元には不透明。
/// </summary>
internal sealed class FrontierSet
{
    private readonly NodeBitSet? _bits;
    private readonly HashSet<long>? _hash;

    private FrontierSet(NodeBitSet bits) { _bits = bits; _hash = null; }
    private FrontierSet(HashSet<long> hash) { _bits = null; _hash = hash; }

    /// <summary>セット内のユニークノード数。</summary>
    public int Count => _bits?.Count ?? _hash!.Count;

    /// <summary><paramref name="nodeId"/> がセットに含まれていれば true。</summary>
    // id→long の平坦化は Sequence (packed Value ではない)。
    public bool Contains(NodeId nodeId)
        => _bits?.Contains(nodeId.Sequence) ?? _hash!.Contains(nodeId.Sequence);

    /// <summary><paramref name="nodes"/> からフロンティアを構築する。ヒューリスティクスで bitset / hashset を選択。</summary>
    public static FrontierSet Build(IEnumerable<NodeId> nodes)
    {
        // First pass into a list to learn min/max/count cheaply.
        var buf = new List<long>(64);
        long max = -1;
        foreach (var n in nodes)
        {
            buf.Add(n.Sequence);
            if (n.Sequence > max) max = n.Sequence;
        }
        return Build(buf, max);
    }

    internal static FrontierSet Build(List<long> ids, long maxId)
    {
        // max id が ~32 * count 以下なら bitmap が安い。bitmap サイズが count * 4 bytes 程度に
        // 収まり HashSet の負荷と同等になる。
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
/// <c>[0, Capacity)</c> 上の dense bitmap。<see cref="FrontierSet"/> の dense-id ケースで使用。
/// <c>(Capacity + 63) / 64</c> 個の ulong を確保する。
/// </summary>
internal sealed class NodeBitSet
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
