using System.Buffers;
using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Wal;

namespace Quiver.Index;

// リーフページ本体 (QUIVER-SW page body):
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
    public const int Body = PagedFile.BodySize;

    // ページ使用バイト数がこの閾値を下回ると under-filled とみなし、
    // 兄弟ページと merge / redistribute を試みる (fill factor ≒ 1/3)。
    // 可変長キーのため「最小キー数」ではなくバイト占有率で判定する。
    public const int MinFill = Body / 3;
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
        // logical-leaf の Delete 論理レコードは LeafDelete 内で「キーが実在する場合のみ」eager 発行する
        // (存在しないキーを log すると、その undo = 旧値再挿入で実在しなかったキーを生んでしまうため)。
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

    public BTreeValueEnumerator Seek(in TKey key) => new(_file, FindLeaf(Encode(key)), Encode(key));

    public BTreeRangeEnumerator Range(in TKey from, bool fromInclusive, in TKey to, bool toInclusive)
        => new(_file, FindLeaf(Encode(from)), Encode(from), fromInclusive, Encode(to), toInclusive);

    public BTreeRangeEnumerator FullScan() => new(_file, LeftmostLeaf(), null, true, null, true);

    public BTreeRawCursor OpenScanCursor(byte[] fromKey, byte[] toKeyInclusive)
        => new(_file, FindLeaf, FindLeaf(fromKey), fromKey, toKeyInclusive);

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

    /// <summary>本索引のバッファプールダーティページを fsync する。</summary>
    public void Flush() => _file.Flush();

    /// <summary>
    /// leaf 連結リストを左端から末尾まで歩いて全 (生キー, 値) ペアを列挙する。
    /// orphan GC は型を意識せずに走査するため、生キーは byte[] のまま渡す。
    /// </summary>
    public IEnumerable<KeyValuePair<byte[], long>> EnumerateRawEntries()
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
                byte[] keyCopy = snap.AsSpan(pos + 2, klen).ToArray();
                long v = BinaryPrimitives.ReadInt64LittleEndian(snap.AsSpan(pos + 2 + klen));
                pos += 2 + klen + 8;
                yield return new KeyValuePair<byte[], long>(keyCopy, v);
            }
            leaf = new PageId(nextLeaf);
        }
    }

    /// <summary>
    /// 生キー版の <see cref="Delete"/>。orphan repair が
    /// <see cref="EnumerateRawEntries"/> から拾った生キーをそのまま削除に使うため、
    /// コーデックの Encode を経由しない。内部は通常の <see cref="Delete"/> と同じく
    /// <c>DeleteDown</c> → エントリカウント減 → root collapse → ヘッダフラッシュ。
    /// </summary>
    public bool DeleteRawEntry(ReadOnlySpan<byte> rawKey, long value)
    {
        // 生キー版は WAL emit しない pure apply (orphan repair = 再 orphan で無害 / recovery = Current null)。
        byte[] kb = rawKey.ToArray();
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
    /// 生キーの **idempotent set** (recovery Pass 2b redo / Pass 3 undo 用)。
    /// 存在すれば値を上書き、無ければ挿入する (state-setting なので二重適用が no-op)。WAL は emit しない
    /// pure apply (recovery 中は active WalWriteSet が無いため page-WAL も出ない)。
    /// </summary>
    public void UpsertRaw(ReadOnlySpan<byte> rawKey, long value)
    {
        byte[] kb = rawKey.ToArray();
        if (TryUpdateValueRaw(kb, value)) return; // 既存キー → in-place 上書き (冪等)
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
        _entryCount++;
        FlushHeader();
    }

    // 生キー kb のエントリが存在すればその値を in-place で上書きし true。無ければ false。
    private bool TryUpdateValueRaw(byte[] kb, long value)
    {
        PageId leaf = FindLeaf(kb);
        int valOff = -1;
        {
            using var rh = _file.PinForRead(leaf);
            ReadOnlySpan<byte> body = rh.Data;
            int count = BinaryPrimitives.ReadInt32LittleEndian(body);
            int pos = BL.LeafHdr;
            for (int i = 0; i < count; i++)
            {
                int klen = BinaryPrimitives.ReadInt16LittleEndian(body[pos..]);
                if (body.Slice(pos + 2, klen).SequenceEqual(kb)) { valOff = pos + 2 + klen; break; }
                pos += 2 + klen + 8;
            }
        }
        if (valOff < 0) return false;
        using var wh = _file.PinForWrite(leaf);
        BinaryPrimitives.WriteInt64LittleEndian(wh.Data[valOff..], value);
        return true;
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
        // Phase 1 (read-only scan): 挿入位置オフセットを特定し、新エントリが収まるかを判定する。
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
            // leaf in-place 挿入も commit 前の PageImage に含める。
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

            // 左ページはインデックス [0, half) を担当する。
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

            // 右ページはインデックス [half, newCount) を担当する。
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

        // Fast path: split 不要 — 後続セパレータを右に詰めてその場で書き込む。
        // セパレータ挿入は子 split に由来する構造変更である。
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

            // 左ページは新インデックス [0, half) をソース pid 上に残す。
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
        int childIdx;
        {
            using var rh = _file.PinForRead(pid);
            int kc = BinaryPrimitives.ReadInt32LittleEndian(rh.Data);
            (child, childIdx) = FindChildWithIndex(rh.Data, kc, key);
        }
        bool ok = DeleteDown(child, key, value, depth + 1);
        if (!ok) return false;

        // 子が under-filled になったら兄弟と merge / redistribute して
        // 縮退させる。merge で空いたページは _file.FreePage で free list へ戻る。
        // この再均衡で親 (pid) のセパレータが減ると、呼び出し元 (祖父) が次に pid の
        // 充填率を検査して再均衡を伝搬させる。root の縮退は Delete/DeleteRawEntry の
        // root-collapse ループが担当する。
        bool childIsLeaf = (depth + 1) == _height - 1;
        if (IsUnderfull(child, childIsLeaf))
            RebalanceChild(pid, childIdx, childIsLeaf);
        return true;
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
    // under-filled ページの merge / redistribute
    // -----------------------------------------------------------------------

    /// <summary>ページの使用バイト数が <see cref="BL.MinFill"/> 未満かを判定する。</summary>
    private bool IsUnderfull(PageId pid, bool isLeaf)
    {
        using var h = _file.PinForRead(pid);
        ReadOnlySpan<byte> body = h.Data;
        int cnt = BinaryPrimitives.ReadInt32LittleEndian(body);
        int pos = isLeaf ? BL.LeafHdr : BL.InternalHdr;
        for (int i = 0; i < cnt; i++)
            pos += 2 + BinaryPrimitives.ReadInt16LittleEndian(body[pos..]) + 8;
        return pos < BL.MinFill;
    }

    /// <summary>
    /// 親 <paramref name="parent"/> の子インデックス <paramref name="ci"/> (0 = firstChild、
    /// i = セパレータ i-1 の子) が under-filled なので、隣接兄弟と merge または
    /// redistribute する。左兄弟を優先し、結合後が 1 ページに収まれば merge、収まらなければ
    /// 1 エントリ移動して親セパレータを更新する。
    /// </summary>
    private void RebalanceChild(PageId parent, int ci, bool childIsLeaf)
    {
        var (firstChild, seps) = DecodeInternal(parent);
        if (seps.Count == 0) return; // 兄弟が居ない退化ノード (上位で merge されるまで放置)

        PageId ChildAt(int idx) => new(idx == 0 ? firstChild : seps[idx - 1].Value);

        if (ci > 0)
        {
            int sepIdx = ci - 1;
            PageId leftPid = ChildAt(ci - 1);
            PageId childPid = ChildAt(ci);
            bool merged = childIsLeaf
                ? TryMergeLeaf(leftPid, childPid)
                : TryMergeInternal(leftPid, childPid, seps[sepIdx].Key);
            if (merged) seps.RemoveAt(sepIdx);
            else if (childIsLeaf) BorrowLeafFromLeft(leftPid, childPid, seps, sepIdx);
            else BorrowInternalFromLeft(leftPid, childPid, seps, sepIdx);
        }
        else
        {
            int sepIdx = 0;
            PageId childPid = ChildAt(0);
            PageId rightPid = ChildAt(1);
            bool merged = childIsLeaf
                ? TryMergeLeaf(childPid, rightPid)
                : TryMergeInternal(childPid, rightPid, seps[sepIdx].Key);
            if (merged) seps.RemoveAt(sepIdx);
            else if (childIsLeaf) BorrowLeafFromRight(childPid, rightPid, seps, sepIdx);
            else BorrowInternalFromRight(childPid, rightPid, seps, sepIdx);
        }

        EncodeInternal(parent, firstChild, seps);
    }

    // --- leaf merge / borrow ---

    /// <summary>左右リーフが 1 ページに収まれば右を左へ統合し、右ページを free する。</summary>
    private bool TryMergeLeaf(PageId leftPid, PageId rightPid)
    {
        var (le, _, lprev) = DecodeLeaf(leftPid);
        var (re, rnext, _) = DecodeLeaf(rightPid);
        int combined = BL.LeafHdr;
        foreach (var e in le) combined += 2 + e.Key.Length + 8;
        foreach (var e in re) combined += 2 + e.Key.Length + 8;
        if (combined > BL.Body) return false;

        le.AddRange(re);
        EncodeLeaf(leftPid, le, rnext, lprev);
        if (rnext >= 0)
        {
            using var nh = _file.PinForWrite(new PageId(rnext));
            BinaryPrimitives.WriteInt64LittleEndian(nh.Data[12..], leftPid.Value);
        }
        _file.FreePage(rightPid);
        return true;
    }

    private void BorrowLeafFromLeft(PageId leftPid, PageId childPid,
        List<KeyValuePair<byte[], long>> seps, int sepIdx)
    {
        var (le, lnext, lprev) = DecodeLeaf(leftPid);
        var (ce, cnext, cprev) = DecodeLeaf(childPid);
        var moved = le[^1];
        le.RemoveAt(le.Count - 1);
        ce.Insert(0, moved);
        EncodeLeaf(leftPid, le, lnext, lprev);
        EncodeLeaf(childPid, ce, cnext, cprev);
        // 親セパレータ (左兄弟 | 子 の境界) = 子の新しい先頭キー、子ポインタは不変。
        seps[sepIdx] = new KeyValuePair<byte[], long>(moved.Key, seps[sepIdx].Value);
    }

    private void BorrowLeafFromRight(PageId childPid, PageId rightPid,
        List<KeyValuePair<byte[], long>> seps, int sepIdx)
    {
        var (ce, cnext, cprev) = DecodeLeaf(childPid);
        var (re, rnext, rprev) = DecodeLeaf(rightPid);
        var moved = re[0];
        re.RemoveAt(0);
        ce.Add(moved);
        EncodeLeaf(childPid, ce, cnext, cprev);
        EncodeLeaf(rightPid, re, rnext, rprev);
        // 親セパレータ (子 | 右兄弟 の境界) = 右兄弟の新しい先頭キー、子ポインタは不変。
        seps[sepIdx] = new KeyValuePair<byte[], long>(re[0].Key, seps[sepIdx].Value);
    }

    // --- internal merge / borrow ---

    /// <summary>
    /// 左右の内部ノードと親セパレータ <paramref name="sepKey"/> が 1 ページに収まれば、
    /// 親セパレータを pull-down しつつ右を左へ統合し、右ページを free する。
    /// </summary>
    private bool TryMergeInternal(PageId leftPid, PageId rightPid, byte[] sepKey)
    {
        var (lfc, lseps) = DecodeInternal(leftPid);
        var (rfc, rseps) = DecodeInternal(rightPid);
        int combined = BL.InternalHdr;
        foreach (var s in lseps) combined += 2 + s.Key.Length + 8;
        combined += 2 + sepKey.Length + 8; // pull-down するセパレータ
        foreach (var s in rseps) combined += 2 + s.Key.Length + 8;
        if (combined > BL.Body) return false;

        lseps.Add(new KeyValuePair<byte[], long>(sepKey, rfc)); // 親セパレータ + 右の firstChild
        lseps.AddRange(rseps);
        EncodeInternal(leftPid, lfc, lseps);
        _file.FreePage(rightPid);
        return true;
    }

    private void BorrowInternalFromLeft(PageId leftPid, PageId childPid,
        List<KeyValuePair<byte[], long>> seps, int sepIdx)
    {
        var (lfc, lseps) = DecodeInternal(leftPid);
        var (cfc, cseps) = DecodeInternal(childPid);
        var lastSep = lseps[^1];
        lseps.RemoveAt(lseps.Count - 1);
        // 右回転: 親セパレータを子の先頭セパレータへ降ろし、子の旧 firstChild を従える。
        cseps.Insert(0, new KeyValuePair<byte[], long>(seps[sepIdx].Key, cfc));
        EncodeInternal(childPid, lastSep.Value, cseps);
        EncodeInternal(leftPid, lfc, lseps);
        seps[sepIdx] = new KeyValuePair<byte[], long>(lastSep.Key, seps[sepIdx].Value);
    }

    private void BorrowInternalFromRight(PageId childPid, PageId rightPid,
        List<KeyValuePair<byte[], long>> seps, int sepIdx)
    {
        var (cfc, cseps) = DecodeInternal(childPid);
        var (rfc, rseps) = DecodeInternal(rightPid);
        var firstRSep = rseps[0];
        rseps.RemoveAt(0);
        // 左回転: 親セパレータを子の末尾セパレータへ降ろし、右の旧 firstChild を従える。
        cseps.Add(new KeyValuePair<byte[], long>(seps[sepIdx].Key, rfc));
        EncodeInternal(childPid, cfc, cseps);
        EncodeInternal(rightPid, firstRSep.Value, rseps);
        seps[sepIdx] = new KeyValuePair<byte[], long>(firstRSep.Key, seps[sepIdx].Value);
    }

    // --- page decode / encode helpers ---

    private (List<KeyValuePair<byte[], long>> entries, long next, long prev) DecodeLeaf(PageId pid)
    {
        using var h = _file.PinForRead(pid);
        ReadOnlySpan<byte> body = h.Data;
        int count = BinaryPrimitives.ReadInt32LittleEndian(body);
        long next = BinaryPrimitives.ReadInt64LittleEndian(body[4..]);
        long prev = BinaryPrimitives.ReadInt64LittleEndian(body[12..]);
        var list = new List<KeyValuePair<byte[], long>>(count);
        int pos = BL.LeafHdr;
        for (int i = 0; i < count; i++)
        {
            int klen = BinaryPrimitives.ReadInt16LittleEndian(body[pos..]);
            byte[] k = body.Slice(pos + 2, klen).ToArray();
            long v = BinaryPrimitives.ReadInt64LittleEndian(body[(pos + 2 + klen)..]);
            list.Add(new KeyValuePair<byte[], long>(k, v));
            pos += 2 + klen + 8;
        }
        return (list, next, prev);
    }

    private void EncodeLeaf(PageId pid, List<KeyValuePair<byte[], long>> entries, long next, long prev)
    {
        // merge/borrow による leaf 全書き換えは構造変更である。
        using var h = _file.PinForWrite(pid);
        Span<byte> body = h.Data;
        body[..BL.Body].Clear();
        BinaryPrimitives.WriteInt32LittleEndian(body, entries.Count);
        BinaryPrimitives.WriteInt64LittleEndian(body[4..], next);
        BinaryPrimitives.WriteInt64LittleEndian(body[12..], prev);
        int pos = BL.LeafHdr;
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteInt16LittleEndian(body[pos..], (short)e.Key.Length);
            e.Key.AsSpan().CopyTo(body[(pos + 2)..]);
            BinaryPrimitives.WriteInt64LittleEndian(body[(pos + 2 + e.Key.Length)..], e.Value);
            pos += 2 + e.Key.Length + 8;
        }
    }

    private (long firstChild, List<KeyValuePair<byte[], long>> seps) DecodeInternal(PageId pid)
    {
        using var h = _file.PinForRead(pid);
        ReadOnlySpan<byte> body = h.Data;
        int kc = BinaryPrimitives.ReadInt32LittleEndian(body);
        long firstChild = BinaryPrimitives.ReadInt64LittleEndian(body[4..]);
        var seps = new List<KeyValuePair<byte[], long>>(kc);
        int pos = BL.InternalHdr;
        for (int i = 0; i < kc; i++)
        {
            int klen = BinaryPrimitives.ReadInt16LittleEndian(body[pos..]);
            byte[] k = body.Slice(pos + 2, klen).ToArray();
            long child = BinaryPrimitives.ReadInt64LittleEndian(body[(pos + 2 + klen)..]);
            seps.Add(new KeyValuePair<byte[], long>(k, child));
            pos += 2 + klen + 8;
        }
        return (firstChild, seps);
    }

    private void EncodeInternal(PageId pid, long firstChild, List<KeyValuePair<byte[], long>> seps)
    {
        using var h = _file.PinForWrite(pid);
        Span<byte> body = h.Data;
        body[..BL.Body].Clear();
        BinaryPrimitives.WriteInt32LittleEndian(body, seps.Count);
        BinaryPrimitives.WriteInt64LittleEndian(body[4..], firstChild);
        int pos = BL.InternalHdr;
        foreach (var s in seps)
        {
            BinaryPrimitives.WriteInt16LittleEndian(body[pos..], (short)s.Key.Length);
            s.Key.AsSpan().CopyTo(body[(pos + 2)..]);
            BinaryPrimitives.WriteInt64LittleEndian(body[(pos + 2 + s.Key.Length)..], s.Value);
            pos += 2 + s.Key.Length + 8;
        }
    }

    // 可変長セパレータキーに対する 2 分探索で、辿るべき子 PageId とその子インデックス
    // (0 = firstChild、i = セパレータ i-1 の子) を求める。<see cref="FindChild"/> の
    // インデックス付き版で、削除時の再均衡が親内の子位置を知るために使う。
    private static (PageId child, int index) FindChildWithIndex(
        ReadOnlySpan<byte> body, int kc, ReadOnlySpan<byte> key)
    {
        if (kc == 0)
            return (new PageId(BinaryPrimitives.ReadInt64LittleEndian(body[4..])), 0);

        Span<int> offsets = kc <= 256 ? stackalloc int[kc] : new int[kc];
        int pos = BL.InternalHdr;
        for (int i = 0; i < kc; i++)
        {
            offsets[i] = pos;
            pos += 2 + BinaryPrimitives.ReadInt16LittleEndian(body[pos..]) + 8;
        }

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
            return (new PageId(BinaryPrimitives.ReadInt64LittleEndian(body[4..])), 0);
        int o = offsets[lo - 1];
        int k = BinaryPrimitives.ReadInt16LittleEndian(body[o..]);
        return (new PageId(BinaryPrimitives.ReadInt64LittleEndian(body[(o + 2 + k)..])), lo);
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

    /// <summary>
    /// abort の before-image undo 後にヘッダから root/entryCount/height を読み直す。
    /// ただし索引が <b>aborted tx 内で新規作成</b>されたケースでは、rollback で backing テナントが
    /// tx 開始前へ巻き戻り、ヘッダは stale before-image (root が範囲外の値) になり得る。その場合は
    /// in-memory 状態を維持する (zombie 索引だが seek は空を返し、次回 reopen でカタログから消える)。
    /// 妥当なヘッダ (root が実データページ範囲内) のときだけ反映する。
    /// </summary>
    public void ReloadFromHeader()
    {
        if (_file.PageCount <= 1) return;
        using var h = _file.PinForRead(HeaderPageId);
        long root = BinaryPrimitives.ReadInt64LittleEndian(h.Data);
        long entryCount = BinaryPrimitives.ReadInt64LittleEndian(h.Data[8..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(h.Data[16..]);
        // root は常に実データページ (>= 2; page 1 はヘッダ)。範囲外 (-1 等) は stale before-image。
        if (root < 2 || root >= _file.PageCount || entryCount < 0 || height < 0) return;
        _root = new PageId(root);
        _entryCount = entryCount;
        _height = height;
    }

    private void LoadHeader()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _root = new PageId(BinaryPrimitives.ReadInt64LittleEndian(h.Data));
        _entryCount = BinaryPrimitives.ReadInt64LittleEndian(h.Data[8..]);
        _height = BinaryPrimitives.ReadInt32LittleEndian(h.Data[16..]);
    }

    // header (root@0 / entryCount@8 / height@16) も他ページと同じ PageImage WAL の対象である。
    // transaction 内の複数更新は最終 after-image へ集約される。
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

internal ref struct BTreeValueEnumerator
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

internal ref struct BTreeRangeEnumerator
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

/// <summary>
/// 生バイトキー範囲に対する forward-only seekable cursor。WAND の
/// document-at-a-time スコアリングに使う。<see cref="BTreeRangeEnumerator"/> と同様に
/// leaf リンクチェーンを歩くが、ヒープオブジェクト (配列に保持可能) であり、
/// <see cref="SeekTo"/> でターゲットが loaded leaf を超えていれば root から O(log N)
/// で降下する (skip-pointer の代替)。
/// </summary>
internal sealed class BTreeRawCursor
{
    private readonly IPagedFile _file;
    private readonly Func<byte[], PageId> _findLeaf;
    private readonly byte[] _upperInclusive;
    private byte[] _lower;
    private bool _needLowerSkip;
    private PageId _nextLeafToLoad;
    private List<(byte[] Key, long Value)>? _entries;
    private int _idx;
    private bool _exhausted;

    internal BTreeRawCursor(IPagedFile file, Func<byte[], PageId> findLeaf, PageId startLeaf,
        byte[] lowerInclusive, byte[] upperInclusive)
    {
        _file = file;
        _findLeaf = findLeaf;
        _lower = lowerInclusive;
        _upperInclusive = upperInclusive;
        _needLowerSkip = true;
        _nextLeafToLoad = startLeaf;
        _exhausted = !startLeaf.IsValid;
    }

    public bool Exhausted => _exhausted;
    public byte[] CurrentKey { get; private set; } = Array.Empty<byte>();
    public long CurrentValue { get; private set; }

    /// <summary>範囲内の次のエントリへ進む。末尾に達したら <c>false</c>。</summary>
    public bool MoveNext()
    {
        while (!_exhausted)
        {
            if (_entries == null)
            {
                if (!_nextLeafToLoad.IsValid) { _exhausted = true; return false; }
                LoadLeaf(_nextLeafToLoad);
            }

            while (_idx < _entries!.Count)
            {
                var (k, v) = _entries[_idx++];
                ReadOnlySpan<byte> ks = k;
                if (_needLowerSkip)
                {
                    if (ks.SequenceCompareTo(_lower) < 0) continue;
                    _needLowerSkip = false;
                }
                if (ks.SequenceCompareTo(_upperInclusive) > 0) { _exhausted = true; return false; }
                CurrentKey = k; CurrentValue = v;
                return true;
            }
            _entries = null; // fall through to load the next leaf
        }
        return false;
    }

    /// <summary>
    /// キーが <paramref name="target"/> 以上の最初のエントリへ移動する
    /// (target は現在位置より前であってはならない)。範囲内に該当エントリが無ければ
    /// <c>false</c>。ターゲットが loaded leaf を超えていれば tree root 経由でジャンプし、
    /// そうでなければ leaf 内を forward scan する。
    /// </summary>
    public bool SeekTo(byte[] target)
    {
        if (_exhausted) return false;
        ReadOnlySpan<byte> t = target;
        if (CurrentKey.Length > 0 && ((ReadOnlySpan<byte>)CurrentKey).SequenceCompareTo(t) >= 0)
            return true;

        // Fast path: target lands inside the loaded leaf (last key already >= target).
        if (_entries != null && _entries.Count > 0 &&
            ((ReadOnlySpan<byte>)_entries[^1].Key).SequenceCompareTo(t) >= 0)
        {
            _needLowerSkip = false;
            while (_idx < _entries.Count)
            {
                var (k, v) = _entries[_idx++];
                ReadOnlySpan<byte> ks = k;
                if (ks.SequenceCompareTo(_upperInclusive) > 0) { _exhausted = true; return false; }
                if (ks.SequenceCompareTo(t) >= 0) { CurrentKey = k; CurrentValue = v; return true; }
            }
        }

        // Slow path: descend from the root to the leaf that would hold target.
        _lower = target;
        _needLowerSkip = true;
        _nextLeafToLoad = _findLeaf(target);
        _entries = null;
        if (!_nextLeafToLoad.IsValid) { _exhausted = true; return false; }
        return MoveNext();
    }

    private void LoadLeaf(PageId leaf)
    {
        using var h = _file.PinForRead(leaf);
        int count = BinaryPrimitives.ReadInt32LittleEndian(h.Data);
        long nextLeaf = BinaryPrimitives.ReadInt64LittleEndian(h.Data[4..]);
        _entries = new List<(byte[], long)>(count);
        int pos = BL.LeafHdr;
        for (int i = 0; i < count; i++)
        {
            int klen = BinaryPrimitives.ReadInt16LittleEndian(h.Data[pos..]);
            byte[] k = h.Data.Slice(pos + 2, klen).ToArray();
            long v = BinaryPrimitives.ReadInt64LittleEndian(h.Data[(pos + 2 + klen)..]);
            _entries.Add((k, v)); pos += 2 + klen + 8;
        }
        _idx = 0;
        _nextLeafToLoad = new PageId(nextLeaf);
    }
}

