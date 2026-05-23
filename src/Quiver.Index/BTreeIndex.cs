using System.Buffers;
using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Index;

// リーフページ本体 (8160 バイト):
//   [0..3]  EntryCount (int32)
//   [4..11] NextLeafPageId (int64、-1 = なし)
//  [12..19] PrevLeafPageId (int64、-1 = なし)
//  [20..]   エントリ列: KeyLen(int16) + Key(可変) + Value(int64)
//
// 内部ページ本体:
//   [0..3]  KeyCount (int32)
//   [4..11] FirstChildPageId (int64)
//  [12..]   セパレータ列: KeyLen(int16) + Key(可変) + ChildPageId(int64)
//
// ヘッダページ (PageId 1) 本体:
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
    // FT-17: 索引名とキー型タグ。Insert / Delete 成功時に IndexUndoContext へ
    // 論理ミューテーションを通知するために保持する。
    private readonly string _name;
    private readonly IndexKeyKind _kind;
    private PageId _root;
    private long _entryCount;
    private int _height;

    internal BTreeIndex(IPagedFile file, IKeyCodec<TKey> codec, string name, IndexKeyKind kind)
    {
        _file = file; _codec = codec; _name = name; _kind = kind;
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
        // FT-17: 進行中の書き込みトランザクションへ論理ミューテーションを通知する。
        // トランザクション外 (バルク構築・recovery 中の逆適用) では no-op。
        IndexUndoContext.Record(_name, _kind, kb, value, isInsert: true);
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
        FlushHeader();
        // FT-17: 削除が成立したときのみ論理ミューテーションを通知する。
        IndexUndoContext.Record(_name, _kind, kb, value, isInsert: false);
        return true;
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

    /// <summary>FT-18: 本索引のバッファプールダーティページを fsync する。</summary>
    public void Flush() => _file.Flush();

    /// <summary>
    /// FT-18: <c>(key, value)</c> ペアが既存ならスキップ、不在なら挿入する。
    /// 索引 redo / undo の冪等再生用 — <see cref="IndexUndoContext"/> へは通知しない。
    /// </summary>
    public bool InsertIfAbsent(in TKey key, long value)
    {
        byte[] kb = Encode(key);
        if (LeafContains(FindLeaf(kb), kb, value)) return false;
        // 通常の Insert ロジックを再利用するが、IndexUndoContext.Record は呼ばない。
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
        return true;
    }

    /// <summary>
    /// FT-18: <c>(key, value)</c> ペアが存在すれば削除、不在なら no-op。
    /// 索引 redo / undo の冪等再生用 — <see cref="IndexUndoContext"/> へは通知しない。
    /// </summary>
    public bool DeleteIfPresent(in TKey key, long value)
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
        FlushHeader();
        return true;
    }

    /// <summary>
    /// FT-18: 指定リーフページに <c>(key, value)</c> ペアが完全一致で存在するか調べる。
    /// 冪等再生の事前検査用。
    /// </summary>
    private bool LeafContains(PageId leaf, byte[] key, long value)
    {
        using var h = _file.PinForRead(leaf);
        ReadOnlySpan<byte> body = h.Data;
        int count = BinaryPrimitives.ReadInt32LittleEndian(body);
        int pos = BL.LeafHdr;
        for (int i = 0; i < count; i++)
        {
            int klen = BinaryPrimitives.ReadInt16LittleEndian(body[pos..]);
            int cmp = body.Slice(pos + 2, klen).SequenceCompareTo(key);
            if (cmp == 0)
            {
                long v = BinaryPrimitives.ReadInt64LittleEndian(body[(pos + 2 + klen)..]);
                if (v == value) return true;
            }
            else if (cmp > 0) return false;
            pos += 2 + klen + 8;
        }
        return false;
    }

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
        // Phase 1 (read-only scan): locate insertion offset and detect whether the new entry fits.
        int count;
        int insOff;        // byte offset within body where the new entry should be written
        int insIdx;        // logical entry index of the new entry within the (count+1) merged sequence
        int oldUsed;       // bytes occupied by existing entries (BL.LeafHdr .. BL.LeafHdr + entriesBytes)
        long oldNext, oldPrev;
        int newEntrySize = 2 + key.Length + 8;

        {
            using var rh = _file.PinForRead(pid);
            ReadOnlySpan<byte> body = rh.Data;
            count = BinaryPrimitives.ReadInt32LittleEndian(body);
            oldNext = BinaryPrimitives.ReadInt64LittleEndian(body[4..]);
            oldPrev = BinaryPrimitives.ReadInt64LittleEndian(body[12..]);

            insOff = -1;
            insIdx = count;
            int pos = BL.LeafHdr;
            for (int i = 0; i < count; i++)
            {
                int klen = BinaryPrimitives.ReadInt16LittleEndian(body[pos..]);
                if (insOff < 0 && _codec.Compare(body.Slice(pos + 2, klen), key) >= 0)
                {
                    insOff = pos;
                    insIdx = i;
                }
                pos += 2 + klen + 8;
            }
            oldUsed = pos;
            if (insOff < 0) insOff = pos;
        }

        // Fast path: 新エントリが現在ページに収まるケース。後続エントリを右に詰めて
        // その場で新エントリを書き込む — スクラッチバッファ無し、エントリ毎の byte[] コピー無し。
        if (oldUsed + newEntrySize <= BL.Body)
        {
            using var wh = _file.PinForWrite(pid);
            Span<byte> body = wh.Data;
            int trailing = oldUsed - insOff;
            if (trailing > 0)
                body.Slice(insOff, trailing).CopyTo(body.Slice(insOff + newEntrySize, trailing));
            BinaryPrimitives.WriteInt16LittleEndian(body[insOff..], (short)key.Length);
            key.AsSpan().CopyTo(body[(insOff + 2)..]);
            BinaryPrimitives.WriteInt64LittleEndian(body[(insOff + 2 + key.Length)..], value);
            BinaryPrimitives.WriteInt32LittleEndian(body, count + 1);
            return null;
        }

        // Split path: ソース本体を一度プールしたスクラッチへスナップショットし、
        // インデックス参照で旧エントリをコピーしつつソースページをインプレースで書き換える。
        int newCount = count + 1;
        int half = newCount / 2;
        PageId rPid = _file.AllocatePage(PageKind.BTreeLeaf);

        byte[] scratch = ArrayPool<byte>.Shared.Rent(BL.Body);
        try
        {
            using (var rh = _file.PinForRead(pid))
                rh.Data[..BL.Body].CopyTo(scratch);

            int srcPos = BL.LeafHdr;

            // 左ページ: インデックス [0, half) を担当
            using (var lph = _file.PinForWrite(pid))
            {
                Span<byte> lbody = lph.Data;
                lbody[..BL.Body].Clear();
                BinaryPrimitives.WriteInt64LittleEndian(lbody[4..], rPid.Value);
                BinaryPrimitives.WriteInt64LittleEndian(lbody[12..], oldPrev);
                int lpos = BL.LeafHdr;
                for (int i = 0; i < half; i++)
                    CopyOrInsertEntry(scratch, ref srcPos, lbody, ref lpos, i, insIdx, key, value);
                BinaryPrimitives.WriteInt32LittleEndian(lbody, half);
            }

            // 中央値キー (右ページの先頭エントリ; new-index = half)。
            byte[] medianBytes;
            if (half == insIdx)
            {
                medianBytes = (byte[])key.Clone();
            }
            else
            {
                int mklen = BinaryPrimitives.ReadInt16LittleEndian(scratch.AsSpan(srcPos));
                medianBytes = scratch.AsSpan(srcPos + 2, mklen).ToArray();
            }

            // 右ページ: インデックス [half, newCount) を担当
            using (var rph = _file.PinForWrite(rPid))
            {
                Span<byte> rbody = rph.Data;
                rbody[..BL.Body].Clear();
                BinaryPrimitives.WriteInt64LittleEndian(rbody[4..], oldNext);
                BinaryPrimitives.WriteInt64LittleEndian(rbody[12..], pid.Value);
                int rpos = BL.LeafHdr;
                for (int i = half; i < newCount; i++)
                    CopyOrInsertEntry(scratch, ref srcPos, rbody, ref rpos, i, insIdx, key, value);
                BinaryPrimitives.WriteInt32LittleEndian(rbody, newCount - half);
            }

            if (oldNext >= 0)
            {
                using var nph = _file.PinForWrite(new PageId(oldNext));
                BinaryPrimitives.WriteInt64LittleEndian(nph.Data[12..], rPid.Value);
            }
            return (medianBytes, rPid);
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }

    // リーフ分割用ヘルパ: new-index i において、新規エントリを出力するかスクラッチから
    // 次のソースエントリをコピーする。旧エントリでは srcPos を進め、いずれの場合も writePos を進める。
    private static void CopyOrInsertEntry(
        byte[] scratch, ref int srcPos,
        Span<byte> dest, ref int writePos,
        int i, int insIdx, byte[] newKey, long newValue)
    {
        if (i == insIdx)
        {
            BinaryPrimitives.WriteInt16LittleEndian(dest[writePos..], (short)newKey.Length);
            newKey.AsSpan().CopyTo(dest[(writePos + 2)..]);
            BinaryPrimitives.WriteInt64LittleEndian(dest[(writePos + 2 + newKey.Length)..], newValue);
            writePos += 2 + newKey.Length + 8;
        }
        else
        {
            int klen = BinaryPrimitives.ReadInt16LittleEndian(scratch.AsSpan(srcPos));
            int entSize = 2 + klen + 8;
            scratch.AsSpan(srcPos, entSize).CopyTo(dest[writePos..]);
            srcPos += entSize;
            writePos += entSize;
        }
    }

    private (byte[] median, PageId right)? InternalInsertSep(PageId pid, byte[] sepKey, PageId newChild)
    {
        // Phase 1: 本体を走査して挿入位置オフセットと総使用バイト数を求める。
        int kc;
        int insOff;
        int insIdx;
        int oldUsed;
        long firstChild;
        int newEntrySize = 2 + sepKey.Length + 8;

        {
            using var rh = _file.PinForRead(pid);
            ReadOnlySpan<byte> body = rh.Data;
            kc = BinaryPrimitives.ReadInt32LittleEndian(body);
            firstChild = BinaryPrimitives.ReadInt64LittleEndian(body[4..]);

            insOff = -1;
            insIdx = kc;
            int pos = BL.InternalHdr;
            for (int i = 0; i < kc; i++)
            {
                int klen = BinaryPrimitives.ReadInt16LittleEndian(body[pos..]);
                if (insOff < 0 && _codec.Compare(body.Slice(pos + 2, klen), sepKey) >= 0)
                {
                    insOff = pos;
                    insIdx = i;
                }
                pos += 2 + klen + 8;
            }
            oldUsed = pos;
            if (insOff < 0) insOff = pos;
        }

        // Fast path: no split — shift trailing separators right and write the new one in place.
        if (oldUsed + newEntrySize <= BL.Body)
        {
            using var wh = _file.PinForWrite(pid);
            Span<byte> body = wh.Data;
            int trailing = oldUsed - insOff;
            if (trailing > 0)
                body.Slice(insOff, trailing).CopyTo(body.Slice(insOff + newEntrySize, trailing));
            BinaryPrimitives.WriteInt16LittleEndian(body[insOff..], (short)sepKey.Length);
            sepKey.AsSpan().CopyTo(body[(insOff + 2)..]);
            BinaryPrimitives.WriteInt64LittleEndian(body[(insOff + 2 + sepKey.Length)..], newChild.Value);
            BinaryPrimitives.WriteInt32LittleEndian(body, kc + 1);
            return null;
        }

        // Split path: 内部分割では中央値を昇格させ (両子から除去)、中央値の子は
        // 右ページの firstChild になる。
        int newCount = kc + 1;
        int half = newCount / 2;

        byte[] scratch = ArrayPool<byte>.Shared.Rent(BL.Body);
        try
        {
            using (var rh = _file.PinForRead(pid))
                rh.Data[..BL.Body].CopyTo(scratch);

            int srcPos = BL.InternalHdr;

            // 左ページ: 新インデックス [0, half) → ソース pid 上に残す。
            using (var lph = _file.PinForWrite(pid))
            {
                Span<byte> lbody = lph.Data;
                lbody[..BL.Body].Clear();
                BinaryPrimitives.WriteInt32LittleEndian(lbody, half);
                BinaryPrimitives.WriteInt64LittleEndian(lbody[4..], firstChild);
                int lpos = BL.InternalHdr;
                for (int i = 0; i < half; i++)
                    CopyOrInsertSep(scratch, ref srcPos, lbody, ref lpos, i, insIdx, sepKey, newChild);
            }

            // 中央値 (new-index half): キーバイト列を呼び出し側に返却、子は右ページの firstChild になる。
            byte[] medianBytes;
            long medianChildValue;
            if (half == insIdx)
            {
                medianBytes = (byte[])sepKey.Clone();
                medianChildValue = newChild.Value;
            }
            else
            {
                int mklen = BinaryPrimitives.ReadInt16LittleEndian(scratch.AsSpan(srcPos));
                medianBytes = scratch.AsSpan(srcPos + 2, mklen).ToArray();
                medianChildValue = BinaryPrimitives.ReadInt64LittleEndian(scratch.AsSpan(srcPos + 2 + mklen));
                srcPos += 2 + mklen + 8;
            }

            // 右ページ: 新インデックス [half+1, newCount)。
            PageId rPid = _file.AllocatePage(PageKind.BTreeInternal);
            using (var rph = _file.PinForWrite(rPid))
            {
                Span<byte> rbody = rph.Data;
                rbody[..BL.Body].Clear();
                BinaryPrimitives.WriteInt32LittleEndian(rbody, newCount - half - 1);
                BinaryPrimitives.WriteInt64LittleEndian(rbody[4..], medianChildValue);
                int rpos = BL.InternalHdr;
                for (int i = half + 1; i < newCount; i++)
                    CopyOrInsertSep(scratch, ref srcPos, rbody, ref rpos, i, insIdx, sepKey, newChild);
            }

            return (medianBytes, rPid);
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }

    // 内部分割用ヘルパ: new-index i において、新規セパレータを出力するかスクラッチから
    // 次のソースセパレータをコピーする。レイアウト: KeyLen(int16) + Key + ChildPageId(int64)。
    private static void CopyOrInsertSep(
        byte[] scratch, ref int srcPos,
        Span<byte> dest, ref int writePos,
        int i, int insIdx, byte[] newKey, PageId newChild)
    {
        if (i == insIdx)
        {
            BinaryPrimitives.WriteInt16LittleEndian(dest[writePos..], (short)newKey.Length);
            newKey.AsSpan().CopyTo(dest[(writePos + 2)..]);
            BinaryPrimitives.WriteInt64LittleEndian(dest[(writePos + 2 + newKey.Length)..], newChild.Value);
            writePos += 2 + newKey.Length + 8;
        }
        else
        {
            int klen = BinaryPrimitives.ReadInt16LittleEndian(scratch.AsSpan(srcPos));
            int entSize = 2 + klen + 8;
            scratch.AsSpan(srcPos, entSize).CopyTo(dest[writePos..]);
            srcPos += entSize;
            writePos += entSize;
        }
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
        int count;
        int delOff = -1;
        int delSize = 0;
        int oldUsed;

        // Phase 1 (read scan): 単一パスで一致する (key, value) エントリと総使用バイト数を求める。
        {
            using var rh = _file.PinForRead(pid);
            ReadOnlySpan<byte> body = rh.Data;
            count = BinaryPrimitives.ReadInt32LittleEndian(body);
            int pos = BL.LeafHdr;
            for (int i = 0; i < count; i++)
            {
                int klen = BinaryPrimitives.ReadInt16LittleEndian(body[pos..]);
                if (delOff < 0 && _codec.Compare(body.Slice(pos + 2, klen), key) == 0)
                {
                    long v = BinaryPrimitives.ReadInt64LittleEndian(body[(pos + 2 + klen)..]);
                    if (v == value)
                    {
                        delOff = pos;
                        delSize = 2 + klen + 8;
                    }
                }
                pos += 2 + klen + 8;
            }
            oldUsed = pos;
        }

        if (delOff < 0) return false;

        using var wh = _file.PinForWrite(pid);
        Span<byte> wbody = wh.Data;
        int trailing = oldUsed - (delOff + delSize);
        if (trailing > 0)
            wbody.Slice(delOff + delSize, trailing).CopyTo(wbody.Slice(delOff, trailing));
        wbody.Slice(oldUsed - delSize, delSize).Clear();
        BinaryPrimitives.WriteInt32LittleEndian(wbody, count - 1);
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

    // 可変長セパレータキーに対する 2 分探索で辿るべき子 PageId を求める。
    // 初回パスでセパレータオフセットを収集し、次にキーで upper-bound 2 分探索する。
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

        // upper-bound 2 分探索: key < sep となる最初のセパレータインデックスを求める
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

    // リーフページを読み出し、その本体スナップショットと次のリーフ PageId 値を返す。
    private (byte[] snap, long nextLeaf) ReadLeafSnap(PageId pid)
    {
        using var h = _file.PinForRead(pid);
        return (h.Data.ToArray(), BinaryPrimitives.ReadInt64LittleEndian(h.Data[4..]));
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

