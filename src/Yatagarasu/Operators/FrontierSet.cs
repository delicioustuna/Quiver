using Yatagarasu.Core;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// <see cref="EdgeScanExpandOperator"/> のプローブ側に使う
/// <see cref="VertexId"/> メンバシップセット。ID が 0 付近に密集する場合は dense bitmap、
/// 疎な場合は <see cref="HashSet{T}"/> の 2 戦略を持ち、呼び出し元には不透明。
/// </summary>
internal sealed class FrontierSet
{
    private readonly VertexBitSet? _bits;
    private readonly Dictionary<long, HashSet<int>>? _generations;
    private readonly HashSet<long>? _hash;
    private readonly int _count;

    private FrontierSet(VertexBitSet bits, Dictionary<long, HashSet<int>> generations, int count)
    {
        _bits = bits;
        _generations = generations;
        _hash = null;
        _count = count;
    }

    private FrontierSet(HashSet<long> hash)
    {
        _bits = null;
        _generations = null;
        _hash = hash;
        _count = hash.Count;
    }

    /// <summary>セット内のユニークVertex数。</summary>
    public int Count => _count;

    /// <summary><paramref name="vertexId"/> がセットに含まれていれば true。</summary>
    public bool Contains(VertexId vertexId)
    {
        if (_bits is null) return _hash!.Contains(vertexId.Value);
        return _bits.Contains(vertexId.Sequence)
            && _generations![vertexId.Sequence].Contains(vertexId.Generation);
    }

    /// <summary><paramref name="vertices"/> からフロンティアを構築する。ヒューリスティクスで bitset / hashset を選択。</summary>
    public static FrontierSet Build(IEnumerable<VertexId> vertices)
    {
        // First pass into a list to learn min/max/count cheaply.
        var buf = new List<VertexId>(64);
        long max = -1;
        foreach (var n in vertices)
        {
            buf.Add(n);
            if (n.Sequence > max) max = n.Sequence;
        }
        return Build(buf, max);
    }

    internal static FrontierSet Build(List<VertexId> ids, long maxId)
    {
        // max id が ~32 * count 以下なら bitmap が安い。bitmap サイズが count * 4 bytes 程度に
        // 収まり HashSet の負荷と同等になる。
        if (maxId >= 0 && maxId <= 32L * Math.Max(ids.Count, 1) && maxId < int.MaxValue)
        {
            var bits = new VertexBitSet((int)maxId + 1);
            var generations = new Dictionary<long, HashSet<int>>();
            int count = 0;
            foreach (var id in ids)
            {
                bits.Add(id.Sequence);
                if (!generations.TryGetValue(id.Sequence, out var values))
                {
                    values = [];
                    generations.Add(id.Sequence, values);
                }
                if (values.Add(id.Generation)) count++;
            }
            return new FrontierSet(bits, generations, count);
        }
        var hash = new HashSet<long>(ids.Count);
        foreach (var id in ids) hash.Add(id.Value);
        return new FrontierSet(hash);
    }
}

/// <summary>
/// <c>[0, Capacity)</c> 上の dense bitmap。<see cref="FrontierSet"/> の dense-id ケースで使用。
/// <c>(Capacity + 63) / 64</c> 個の ulong を確保する。
/// </summary>
internal sealed class VertexBitSet
{
    private readonly ulong[] _words;
    private int _count;

    public int Capacity { get; }
    public int Count => _count;

    public VertexBitSet(int capacity)
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
