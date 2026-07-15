using System.Collections.Concurrent;
using System.Threading;

namespace Quiver.Storage.Records;

/// <summary>
/// 1 DB 内の全 vector index が共有する payload cache のメモリ予算。
/// 登録時だけ排他し、hit 経路は second-chance bit の更新だけで通す。
/// </summary>
internal sealed class VectorPayloadCacheBudget
{
    private readonly long _limitBytes;
    private readonly Lock _gate = new();
    private readonly LinkedList<Entry> _entries = new();
    private long _usedBytes;

    public VectorPayloadCacheBudget(long limitBytes)
        => _limitBytes = Math.Max(0, limitBytes);

    public bool Enabled => _limitBytes > 0;

    public bool CanFit(long bytes) => bytes > 0 && bytes <= _limitBytes;

    public bool TryRegister(Entry entry)
    {
        if (!CanFit(entry.SizeBytes)) return false;

        List<Action>? evictions = null;
        lock (_gate)
        {
            while (_usedBytes + entry.SizeBytes > _limitBytes && _entries.Count > 0)
            {
                // CLOCK に近い second-chance。hit は lock を取らず bit を立て、
                // slab 登録時だけ最近使われた entry を末尾へ送る。
                int probes = _entries.Count;
                while (probes-- > 0)
                {
                    var candidate = _entries.First!.Value;
                    if (Interlocked.Exchange(ref candidate.RecentlyUsed, 0) == 0) break;
                    _entries.RemoveFirst();
                    _entries.AddLast(candidate.Node!);
                }

                var victim = _entries.First!.Value;
                RemoveLocked(victim);
                (evictions ??= []).Add(victim.Evict);
            }

            entry.Node = _entries.AddLast(entry);
            entry.Registered = true;
            _usedBytes += entry.SizeBytes;
        }

        if (evictions is not null)
            foreach (var evict in evictions) evict();
        return true;
    }

    public void Touch(Entry? entry)
    {
        if (entry is not null && Volatile.Read(ref entry.Registered))
            Volatile.Write(ref entry.RecentlyUsed, 1);
    }

    public void Unregister(Entry? entry)
    {
        if (entry is null) return;
        lock (_gate)
        {
            if (entry.Registered) RemoveLocked(entry);
        }
    }

    private void RemoveLocked(Entry entry)
    {
        if (!entry.Registered) return;
        _entries.Remove(entry.Node!);
        entry.Node = null;
        entry.Registered = false;
        _usedBytes -= entry.SizeBytes;
    }

    internal sealed class Entry(long sizeBytes, Action evict)
    {
        internal long SizeBytes { get; } = sizeBytes;
        internal Action Evict { get; } = evict;
        internal LinkedListNode<Entry>? Node;
        internal bool Registered;
        internal int RecentlyUsed;
    }
}

/// <summary>
/// seq range ごとの固定長 slab。未参照 range は確保せず、予算超過時は page 読み出しへ戻る。
/// state は vector のコピー完了後に publish するため、将来 reader lock を分離しても
/// half-written cache entry を観測しない。
/// </summary>
internal sealed class VectorPayloadCache
{
    // HNSW は seq 空間をランダムに辿る。大きな slab は 1 vector のために大量の未使用領域を
    // 常駐させるため、1 slab をおよそ 64 KiB に抑える。
    private const int TargetSlabBytes = 64 * 1024;
    private const byte Unloaded = 0;
    private const byte Absent = 1;
    private const byte Present = 2;

    private readonly int _dim;
    private readonly int _vectorsPerSlab;
    private readonly VectorPayloadCacheBudget _budget;
    private readonly ConcurrentDictionary<long, Slab> _slabs = new();

    public VectorPayloadCache(int dim, VectorPayloadCacheBudget budget)
    {
        _dim = dim;
        _budget = budget;
        int bytesPerVector = checked(dim * sizeof(float) + sizeof(ushort) + sizeof(byte));
        _vectorsPerSlab = Math.Max(1, TargetSlabBytes / bytesPerVector);
    }

    public Lookup TryGet(long seq, Span<float> destination, out ushort generation)
    {
        var lookup = TryGetSpan(seq, out var vector, out generation);
        if (lookup == Lookup.Present) vector.CopyTo(destination);
        return lookup;
    }

    /// <summary>
    /// HNSW scorer 用の copy-free lookup。slab が directory から evict されても、
    /// 返した span が参照する配列自体は scoring 完了まで GC root される。
    /// </summary>
    public Lookup TryGetSpan(
        long seq,
        out ReadOnlySpan<float> vector,
        out ushort generation)
    {
        vector = default;
        generation = 0;
        if (!_budget.Enabled) return Lookup.Miss;
        long slabId = seq / _vectorsPerSlab;
        if (!_slabs.TryGetValue(slabId, out var slab)) return Lookup.Miss;

        _budget.Touch(slab.BudgetEntry);
        int offset = (int)(seq % _vectorsPerSlab);
        byte state = Volatile.Read(ref slab.States[offset]);
        if (state == Unloaded) return Lookup.Miss;
        if (state == Absent) return Lookup.Absent;

        generation = slab.Generations[offset];
        vector = slab.Values.AsSpan(offset * _dim, _dim);
        return Lookup.Present;
    }

    public void StorePresent(long seq, ushort generation, ReadOnlySpan<float> vector)
    {
        var slab = GetOrCreate(seq);
        if (slab is null) return;
        int offset = (int)(seq % _vectorsPerSlab);
        vector.CopyTo(slab.Values.AsSpan(offset * _dim, _dim));
        slab.Generations[offset] = generation;
        Volatile.Write(ref slab.States[offset], Present);
        _budget.Touch(slab.BudgetEntry);
    }

    public void StoreAbsent(long seq)
    {
        var slab = GetOrCreate(seq);
        if (slab is null) return;
        int offset = (int)(seq % _vectorsPerSlab);
        Volatile.Write(ref slab.States[offset], Absent);
        _budget.Touch(slab.BudgetEntry);
    }

    public void Clear()
    {
        foreach (var (slabId, slab) in _slabs)
        {
            if (_slabs.TryRemove(new KeyValuePair<long, Slab>(slabId, slab)))
                _budget.Unregister(slab.BudgetEntry);
        }
    }

    private Slab? GetOrCreate(long seq)
    {
        if (!_budget.Enabled) return null;
        long slabId = seq / _vectorsPerSlab;
        if (_slabs.TryGetValue(slabId, out var existing)) return existing;

        var created = new Slab(_vectorsPerSlab, _dim);
        if (!_budget.CanFit(created.SizeBytes)) return null;
        if (!_slabs.TryAdd(slabId, created))
            return _slabs.TryGetValue(slabId, out existing) ? existing : null;

        created.BudgetEntry = new VectorPayloadCacheBudget.Entry(
            created.SizeBytes,
            () => _slabs.TryRemove(new KeyValuePair<long, Slab>(slabId, created)));
        if (_budget.TryRegister(created.BudgetEntry)) return created;

        _slabs.TryRemove(new KeyValuePair<long, Slab>(slabId, created));
        return null;
    }

    internal enum Lookup : byte
    {
        Miss,
        Absent,
        Present,
    }

    private sealed class Slab
    {
        public Slab(int count, int dim)
        {
            Values = new float[checked(count * dim)];
            Generations = new ushort[count];
            States = new byte[count];
            SizeBytes = checked(
                (long)Values.Length * sizeof(float) +
                (long)Generations.Length * sizeof(ushort) +
                States.Length);
        }

        public float[] Values { get; }
        public ushort[] Generations { get; }
        public byte[] States { get; }
        public long SizeBytes { get; }
        public VectorPayloadCacheBudget.Entry? BudgetEntry;
    }
}
