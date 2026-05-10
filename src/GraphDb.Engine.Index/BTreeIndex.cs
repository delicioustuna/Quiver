using System.Buffers.Binary;
using GraphDb.Engine.Core;
using GraphDb.Engine.Storage;

namespace GraphDb.Engine.Index;

// Leaf page body (8160 bytes):
//   [0..3]  EntryCount (int32)
//   [4..11] NextLeafPageId (int64, -1 = none)
//  [12..19] PrevLeafPageId (int64, -1 = none)
//  [20..]   entries: KeyLen(int16) + Key(var) + Value(int64)
//
// Internal page body:
//   [0..3]  KeyCount (int32)
//   [4..11] FirstChildPageId (int64)
//  [12..]   separators: KeyLen(int16) + Key(var) + ChildPageId(int64)
//
// Header page (PageId 1) body:
//   [0..7]  RootPageId
//   [8..15] EntryCount
//  [16..19] Height

internal static class BL // BTreeLayout
{
    public const int LeafHdr = 20;
    public const int InternalHdr = 12;
    public const int Body = 8160;
}

internal sealed class BTreeIndex<TKey> : IBTreeIndex<TKey>
{
    private static readonly PageId HeaderPageId = new(1);

    private readonly IPagedFile _file;
    private readonly IKeyCodec<TKey> _codec;
    private PageId _root;
    private long _entryCount;
    private int _height;

    internal BTreeIndex(IPagedFile file, IKeyCodec<TKey> codec)
    {
        _file = file; _codec = codec;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _root = _file.AllocatePage(PageKind.BTreeLeaf);
            InitLeafPage(_root);
            _entryCount = 0; _height = 1;
            FlushHeader();
        }
        else LoadHeader();
    }

    public int Height => _height;
    public long EntryCount => _entryCount;

    public void Insert(in TKey key, long value)
    {
        byte[] kb = Encode(key);
        var split = InsertDown(_root, kb, value, 0);
        if (split.HasValue)
        {
            PageId newRoot = _file.AllocatePage(PageKind.BTreeInternal);
            var ph = _file.PinForWrite(newRoot);
            ph.Data.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(ph.Data, 1);
            BinaryPrimitives.WriteInt64LittleEndian(ph.Data[4..], _root.Value);
            int p = BL.InternalHdr;
            WriteSep(ph.Data, ref p, split.Value.median, split.Value.right);
            ph.Dispose();
            _root = newRoot; _height++;
        }
        _entryCount++; FlushHeader();
    }

    public bool Delete(in TKey key, long value)
    {
        byte[] kb = Encode(key);
        bool ok = DeleteDown(_root, kb, value, 0);
        if (!ok) return false;
        _entryCount--;
        while (_height > 1)
        {
            using var rh = _file.PinForRead(_root);
            if (BinaryPrimitives.ReadInt32LittleEndian(rh.Data) != 0) break;
            PageId onlyChild = new(BinaryPrimitives.ReadInt64LittleEndian(rh.Data[4..]));
            _file.FreePage(_root);
            _root = onlyChild; _height--;
        }
        FlushHeader(); return true;
    }

    public BTreeValueEnumerator Seek(in TKey key) => new(_file, FindLeaf(Encode(key)), Encode(key));

    public BTreeRangeEnumerator Range(in TKey from, bool fromInclusive, in TKey to, bool toInclusive)
        => new(_file, FindLeaf(Encode(from)), Encode(from), fromInclusive, Encode(to), toInclusive);

    public BTreeRangeEnumerator FullScan() => new(_file, LeftmostLeaf(), null, true, null, true);

    public IEnumerable<long> SeekValues(TKey key)
    {
        byte[] kb = Encode(key);
        PageId leaf = FindLeaf(kb);
        var (snap, _) = ReadLeafSnap(leaf);
        int count = BinaryPrimitives.ReadInt32LittleEndian(snap);
        int pos = BL.LeafHdr;
        for (int i = 0; i < count; i++)
        {
            int klen = BinaryPrimitives.ReadInt16LittleEndian(snap.AsSpan(pos));
            long v = BinaryPrimitives.ReadInt64LittleEndian(snap.AsSpan(pos + 2 + klen));
            int cmp = snap.AsSpan(pos + 2, klen).SequenceCompareTo(kb);
            pos += 2 + klen + 8;
            if (cmp < 0) continue;
            if (cmp > 0) yield break;
            yield return v;
        }
    }

    public IEnumerable<long> RangeValues(TKey from, bool fromInclusive, TKey to, bool toInclusive)
    {
        byte[] fromKb = Encode(from);
        byte[] toKb = Encode(to);
        PageId leaf = FindLeaf(fromKb);
        while (leaf.IsValid)
        {
            var (snap, nextLeaf) = ReadLeafSnap(leaf);
            int count = BinaryPrimitives.ReadInt32LittleEndian(snap);
            int pos = BL.LeafHdr;
            for (int i = 0; i < count; i++)
            {
                int klen = BinaryPrimitives.ReadInt16LittleEndian(snap.AsSpan(pos));
                long v = BinaryPrimitives.ReadInt64LittleEndian(snap.AsSpan(pos + 2 + klen));
                int cmpFrom = snap.AsSpan(pos + 2, klen).SequenceCompareTo(fromKb);
                int cmpTo = snap.AsSpan(pos + 2, klen).SequenceCompareTo(toKb);
                pos += 2 + klen + 8;
                if (fromInclusive ? cmpFrom < 0 : cmpFrom <= 0) continue;
                if (toInclusive ? cmpTo > 0 : cmpTo >= 0) yield break;
                yield return v;
            }
            leaf = new PageId(nextLeaf);
        }
    }

    public IEnumerable<long> AllValues()
    {
        PageId leaf = LeftmostLeaf();
        while (leaf.IsValid)
        {
            var (snap, nextLeaf) = ReadLeafSnap(leaf);
            int count = BinaryPrimitives.ReadInt32LittleEndian(snap);
            int pos = BL.LeafHdr;
            for (int i = 0; i < count; i++)
            {
                int klen = BinaryPrimitives.ReadInt16LittleEndian(snap.AsSpan(pos));
                long v = BinaryPrimitives.ReadInt64LittleEndian(snap.AsSpan(pos + 2 + klen));
                pos += 2 + klen + 8;
                yield return v;
            }
            leaf = new PageId(nextLeaf);
        }
    }

    public void Dispose() => _file.Dispose();

    // -----------------------------------------------------------------------

    private (byte[] median, PageId right)? InsertDown(PageId pid, byte[] key, long value, int depth)
    {
        if (depth == _height - 1) return LeafInsert(pid, key, value);
        PageId child;
        {
            using var rh = _file.PinForRead(pid);
            int kc = BinaryPrimitives.ReadInt32LittleEndian(rh.Data);
            child = FindChild(rh.Data, kc, key);
        }
        var split = InsertDown(child, key, value, depth + 1);
        if (split == null) return null;
        return InternalInsertSep(pid, split.Value.median, split.Value.right);
    }

    private (byte[] median, PageId right)? LeafInsert(PageId pid, byte[] key, long value)
    {
        byte[] snap;
        { using var rh = _file.PinForRead(pid); snap = rh.Data.ToArray(); }

        int count = BinaryPrimitives.ReadInt32LittleEndian(snap);
        var entries = ReadLeafEntries(snap, count);
        int ins = 0;
        while (ins < entries.Count && CompareBytes(entries[ins].k, key) < 0) ins++;
        entries.Insert(ins, (key, value));

        int needed = BL.LeafHdr;
        foreach (var (k, _) in entries) needed += 2 + k.Length + 8;

        long oldNext = BinaryPrimitives.ReadInt64LittleEndian(snap.AsSpan(4));
        long oldPrev = BinaryPrimitives.ReadInt64LittleEndian(snap.AsSpan(12));

        if (needed <= BL.Body)
        {
            var ph = _file.PinForWrite(pid);
            ph.Data[..BL.Body].Clear();
            BinaryPrimitives.WriteInt64LittleEndian(ph.Data[4..], oldNext);
            BinaryPrimitives.WriteInt64LittleEndian(ph.Data[12..], oldPrev);
            int pos = BL.LeafHdr;
            foreach (var (k, v) in entries) WriteEntry(ph.Data, ref pos, k, v);
            BinaryPrimitives.WriteInt32LittleEndian(ph.Data, entries.Count);
            ph.Dispose();
            return null;
        }

        int half = entries.Count / 2;
        PageId rPid = _file.AllocatePage(PageKind.BTreeLeaf);

        var lph = _file.PinForWrite(pid);
        lph.Data[..BL.Body].Clear();
        BinaryPrimitives.WriteInt64LittleEndian(lph.Data[4..], rPid.Value);
        BinaryPrimitives.WriteInt64LittleEndian(lph.Data[12..], oldPrev);
        int lpos = BL.LeafHdr;
        for (int i = 0; i < half; i++) WriteEntry(lph.Data, ref lpos, entries[i].k, entries[i].v);
        BinaryPrimitives.WriteInt32LittleEndian(lph.Data, half);
        lph.Dispose();

        var rph = _file.PinForWrite(rPid);
        rph.Data[..BL.Body].Clear();
        BinaryPrimitives.WriteInt64LittleEndian(rph.Data[4..], oldNext);
        BinaryPrimitives.WriteInt64LittleEndian(rph.Data[12..], pid.Value);
        int rpos = BL.LeafHdr;
        for (int i = half; i < entries.Count; i++) WriteEntry(rph.Data, ref rpos, entries[i].k, entries[i].v);
        BinaryPrimitives.WriteInt32LittleEndian(rph.Data, entries.Count - half);
        rph.Dispose();

        if (oldNext >= 0)
        {
            var nph = _file.PinForWrite(new PageId(oldNext));
            BinaryPrimitives.WriteInt64LittleEndian(nph.Data[12..], rPid.Value);
            nph.Dispose();
        }
        return (entries[half].k, rPid);
    }

    private (byte[] median, PageId right)? InternalInsertSep(PageId pid, byte[] sepKey, PageId newChild)
    {
        byte[] snap;
        { using var rh = _file.PinForRead(pid); snap = rh.Data.ToArray(); }

        int kc = BinaryPrimitives.ReadInt32LittleEndian(snap);
        var seps = ReadInternalSeps(snap, kc);
        long firstChild = BinaryPrimitives.ReadInt64LittleEndian(snap.AsSpan(4));

        int ins = 0;
        while (ins < seps.Count && CompareBytes(seps[ins].k, sepKey) < 0) ins++;
        seps.Insert(ins, (sepKey, newChild));

        int needed = BL.InternalHdr;
        foreach (var (k, _) in seps) needed += 2 + k.Length + 8;

        if (needed <= BL.Body)
        {
            var ph = _file.PinForWrite(pid);
            ph.Data[..BL.Body].Clear();
            BinaryPrimitives.WriteInt32LittleEndian(ph.Data, seps.Count);
            BinaryPrimitives.WriteInt64LittleEndian(ph.Data[4..], firstChild);
            int pos = BL.InternalHdr;
            foreach (var (k, c) in seps) WriteSep(ph.Data, ref pos, k, c);
            ph.Dispose();
            return null;
        }

        int half = seps.Count / 2;
        byte[] median = seps[half].k;
        PageId medianChild = seps[half].child;

        var lph = _file.PinForWrite(pid);
        lph.Data[..BL.Body].Clear();
        BinaryPrimitives.WriteInt32LittleEndian(lph.Data, half);
        BinaryPrimitives.WriteInt64LittleEndian(lph.Data[4..], firstChild);
        int lpos = BL.InternalHdr;
        for (int i = 0; i < half; i++) WriteSep(lph.Data, ref lpos, seps[i].k, seps[i].child);
        lph.Dispose();

        PageId rPid = _file.AllocatePage(PageKind.BTreeInternal);
        var rph = _file.PinForWrite(rPid);
        rph.Data[..BL.Body].Clear();
        BinaryPrimitives.WriteInt32LittleEndian(rph.Data, seps.Count - half - 1);
        BinaryPrimitives.WriteInt64LittleEndian(rph.Data[4..], medianChild.Value);
        int rpos = BL.InternalHdr;
        for (int i = half + 1; i < seps.Count; i++) WriteSep(rph.Data, ref rpos, seps[i].k, seps[i].child);
        rph.Dispose();

        return (median, rPid);
    }

    private bool DeleteDown(PageId pid, byte[] key, long value, int depth)
    {
        if (depth == _height - 1) return LeafDelete(pid, key, value);
        PageId child;
        {
            using var rh = _file.PinForRead(pid);
            int kc = BinaryPrimitives.ReadInt32LittleEndian(rh.Data);
            child = FindChild(rh.Data, kc, key);
        }
        return DeleteDown(child, key, value, depth + 1);
    }

    private bool LeafDelete(PageId pid, byte[] key, long value)
    {
        byte[] snap;
        { using var rh = _file.PinForRead(pid); snap = rh.Data.ToArray(); }
        int count = BinaryPrimitives.ReadInt32LittleEndian(snap);
        var entries = ReadLeafEntries(snap, count);
        int found = -1;
        for (int i = 0; i < entries.Count; i++)
            if (CompareBytes(entries[i].k, key) == 0 && entries[i].v == value) { found = i; break; }
        if (found < 0) return false;
        entries.RemoveAt(found);
        var ph = _file.PinForWrite(pid);
        ph.Data[..BL.Body].Clear();
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[4..], BinaryPrimitives.ReadInt64LittleEndian(snap.AsSpan(4)));
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[12..], BinaryPrimitives.ReadInt64LittleEndian(snap.AsSpan(12)));
        int pos = BL.LeafHdr;
        foreach (var (k, v) in entries) WriteEntry(ph.Data, ref pos, k, v);
        BinaryPrimitives.WriteInt32LittleEndian(ph.Data, entries.Count);
        ph.Dispose();
        return true;
    }

    // -----------------------------------------------------------------------

    private PageId FindLeaf(byte[] key)
    {
        PageId cur = _root;
        ReadOnlySpan<byte> keySpan = key;
        for (int d = 0; d < _height - 1; d++)
        {
            using var h = _file.PinForRead(cur);
            int kc = BinaryPrimitives.ReadInt32LittleEndian(h.Data);
            cur = FindChild(h.Data, kc, keySpan);
        }
        return cur;
    }

    private PageId LeftmostLeaf()
    {
        PageId cur = _root;
        for (int d = 0; d < _height - 1; d++)
        {
            using var h = _file.PinForRead(cur);
            cur = new PageId(BinaryPrimitives.ReadInt64LittleEndian(h.Data[4..]));
        }
        return cur;
    }

    // Binary search on variable-length separator keys to find the child PageId to follow.
    // Collects separator offsets in a first pass, then binary-searches (upper-bound) for the key.
    private static PageId FindChild(ReadOnlySpan<byte> body, int kc, ReadOnlySpan<byte> key)
    {
        if (kc == 0)
            return new PageId(BinaryPrimitives.ReadInt64LittleEndian(body[4..]));

        Span<int> offsets = kc <= 256 ? stackalloc int[kc] : new int[kc];
        int pos = BL.InternalHdr;
        for (int i = 0; i < kc; i++)
        {
            offsets[i] = pos;
            pos += 2 + BinaryPrimitives.ReadInt16LittleEndian(body[pos..]) + 8;
        }

        // Upper-bound binary search: find first sep index where key < sep
        int lo = 0, hi = kc;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            int sepOff = offsets[mid];
            int klen = BinaryPrimitives.ReadInt16LittleEndian(body[sepOff..]);
            if (key.SequenceCompareTo(body.Slice(sepOff + 2, klen)) < 0)
                hi = mid;
            else
                lo = mid + 1;
        }

        if (lo == 0)
            return new PageId(BinaryPrimitives.ReadInt64LittleEndian(body[4..]));
        int o = offsets[lo - 1];
        int k = BinaryPrimitives.ReadInt16LittleEndian(body[o..]);
        return new PageId(BinaryPrimitives.ReadInt64LittleEndian(body[(o + 2 + k)..]));
    }

    // Reads a leaf page and returns its body snapshot and the next-leaf PageId value.
    private (byte[] snap, long nextLeaf) ReadLeafSnap(PageId pid)
    {
        using var h = _file.PinForRead(pid);
        return (h.Data.ToArray(), BinaryPrimitives.ReadInt64LittleEndian(h.Data[4..]));
    }

    private static List<(byte[] k, long v)> ReadLeafEntries(byte[] body, int count)
    {
        var list = new List<(byte[], long)>(count);
        int pos = BL.LeafHdr;
        for (int i = 0; i < count; i++)
        {
            int klen = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(pos));
            byte[] k = body[(pos + 2)..(pos + 2 + klen)];
            long v = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(pos + 2 + klen));
            list.Add((k, v)); pos += 2 + klen + 8;
        }
        return list;
    }

    private static List<(byte[] k, PageId child)> ReadInternalSeps(byte[] body, int kc)
    {
        var list = new List<(byte[], PageId)>(kc);
        int pos = BL.InternalHdr;
        for (int i = 0; i < kc; i++)
        {
            int klen = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(pos));
            byte[] k = body[(pos + 2)..(pos + 2 + klen)];
            PageId child = new(BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(pos + 2 + klen)));
            list.Add((k, child)); pos += 2 + klen + 8;
        }
        return list;
    }

    private static void WriteEntry(Span<byte> dest, ref int pos, byte[] key, long value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(dest[pos..], (short)key.Length);
        key.AsSpan().CopyTo(dest[(pos + 2)..]);
        BinaryPrimitives.WriteInt64LittleEndian(dest[(pos + 2 + key.Length)..], value);
        pos += 2 + key.Length + 8;
    }

    private static void WriteSep(Span<byte> dest, ref int pos, byte[] key, PageId child)
    {
        BinaryPrimitives.WriteInt16LittleEndian(dest[pos..], (short)key.Length);
        key.AsSpan().CopyTo(dest[(pos + 2)..]);
        BinaryPrimitives.WriteInt64LittleEndian(dest[(pos + 2 + key.Length)..], child.Value);
        pos += 2 + key.Length + 8;
    }

    private void InitLeafPage(PageId pid)
    {
        var ph = _file.PinForWrite(pid);
        ph.Data[..BL.Body].Clear();
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[4..], -1L);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[12..], -1L);
        ph.Dispose();
    }

    private int CompareBytes(byte[] a, byte[] b) => _codec.Compare(a, b);

    private byte[] Encode(in TKey key)
    {
        byte[] buf = new byte[_codec.GetEncodedSize(key)];
        _codec.Encode(key, buf);
        return buf;
    }

    private void LoadHeader()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _root = new PageId(BinaryPrimitives.ReadInt64LittleEndian(h.Data));
        _entryCount = BinaryPrimitives.ReadInt64LittleEndian(h.Data[8..]);
        _height = BinaryPrimitives.ReadInt32LittleEndian(h.Data[16..]);
    }

    private void FlushHeader()
    {
        var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data, _root.Value);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[8..], _entryCount);
        BinaryPrimitives.WriteInt32LittleEndian(ph.Data[16..], _height);
        ph.Dispose();
    }
}

// -----------------------------------------------------------------------
// Enumerators
// -----------------------------------------------------------------------

public ref struct BTreeValueEnumerator
{
    private readonly IPagedFile _file;
    private readonly PageId _leaf;
    private readonly byte[] _key;
    private List<(byte[], long)>? _entries;
    private int _index;
    private long _current;

    internal BTreeValueEnumerator(IPagedFile file, PageId leaf, byte[] key)
    {
        _file = file; _leaf = leaf; _key = key; _entries = null; _index = 0; _current = 0;
    }

    public bool MoveNext()
    {
        if (_entries == null) LoadEntries();
        while (_index < _entries!.Count)
        {
            var (k, v) = _entries[_index++];
            int cmp = ((ReadOnlySpan<byte>)k).SequenceCompareTo(_key);
            if (cmp == 0) { _current = v; return true; }
            if (cmp > 0) return false;
        }
        return false;
    }

    private void LoadEntries()
    {
        using var h = _file.PinForRead(_leaf);
        int count = BinaryPrimitives.ReadInt32LittleEndian(h.Data);
        _entries = new List<(byte[], long)>(count);
        int pos = BL.LeafHdr;
        for (int i = 0; i < count; i++)
        {
            int klen = BinaryPrimitives.ReadInt16LittleEndian(h.Data[pos..]);
            byte[] k = h.Data.Slice(pos + 2, klen).ToArray();
            long v = BinaryPrimitives.ReadInt64LittleEndian(h.Data[(pos + 2 + klen)..]);
            _entries.Add((k, v)); pos += 2 + klen + 8;
        }
    }

    public long Current => _current;
    public void Dispose() { }
}

public ref struct BTreeRangeEnumerator
{
    private readonly IPagedFile _file;
    private PageId _leaf;
    private readonly byte[]? _from;
    private readonly bool _fromInclusive;
    private readonly byte[]? _to;
    private readonly bool _toInclusive;
    private KeyValueEntry _current;
    private bool _done;
    private List<(byte[], long)>? _pageEntries;
    private int _pageIndex;

    internal BTreeRangeEnumerator(IPagedFile file, PageId leaf,
        byte[]? from, bool fromInclusive, byte[]? to, bool toInclusive)
    {
        _file = file; _leaf = leaf;
        _from = from; _fromInclusive = fromInclusive;
        _to = to; _toInclusive = toInclusive;
        _done = !leaf.IsValid; _pageEntries = null; _pageIndex = 0; _current = default;
    }

    public bool MoveNext()
    {
        while (!_done)
        {
            if (_pageEntries == null)
            {
                if (!_leaf.IsValid) { _done = true; return false; }
                long nextLeaf;
                using (var h = _file.PinForRead(_leaf))
                {
                    int count = BinaryPrimitives.ReadInt32LittleEndian(h.Data);
                    nextLeaf = BinaryPrimitives.ReadInt64LittleEndian(h.Data[4..]);
                    _pageEntries = new List<(byte[], long)>(count);
                    int pos = BL.LeafHdr;
                    for (int i = 0; i < count; i++)
                    {
                        int klen = BinaryPrimitives.ReadInt16LittleEndian(h.Data[pos..]);
                        byte[] k = h.Data.Slice(pos + 2, klen).ToArray();
                        long v = BinaryPrimitives.ReadInt64LittleEndian(h.Data[(pos + 2 + klen)..]);
                        _pageEntries.Add((k, v)); pos += 2 + klen + 8;
                    }
                }
                _pageIndex = 0;
                _leaf = new PageId(nextLeaf);
            }

            while (_pageIndex < _pageEntries.Count)
            {
                var (k, v) = _pageEntries[_pageIndex++];
                ReadOnlySpan<byte> ks = k;

                if (_from != null)
                {
                    int c = ks.SequenceCompareTo(_from);
                    if (_fromInclusive ? c < 0 : c <= 0) continue;
                }
                if (_to != null)
                {
                    int c = ks.SequenceCompareTo(_to);
                    if (_toInclusive ? c > 0 : c >= 0) { _done = true; return false; }
                }
                _current = new KeyValueEntry(k, v);
                return true;
            }
            _pageEntries = null;
        }
        return false;
    }

    public KeyValueEntry Current => _current;
    public void Dispose() { }
}

