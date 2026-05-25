using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Stores;

// FT-26 v2 (MVCC) property record layout (57 バイト):
//  0 Flags(1) | 1 KeyId(4) | 5 ValueType(1) | 6 InlineValue(24) |
// 30 SpilloverId(5) | 35 NextPropId(6) | 41 Xmin(8) | 49 Xmax(8)
//
// Xmin / Xmax = TransactionId.Value (long)。0 = unset。
//   Create: xmin = MvccContext.CurrentTxId, xmax = 0、chain head に prepend
//   Delete (論理): xmax = MvccContext.CurrentTxId
//     チェーンは unlink せず slot も free list に戻さない。snapshot reader が辿れる。
//     SetProperty (= 上書き) は「既存 prop に xmax スタンプ + 新 prop を head に挿入」。
//     enumerate は invisible record を skip するので、key 検索で最初に当たる visible が新値。
internal sealed class PropertyStore : IPropertyStore
{
    public const int RecordSize = 57;
    private const byte FlagInUse = 0x01;
    private const byte FlagSpillover = 0x02;
    private const int InlineCapacity = 24;
    internal const int XminOffset = 41;
    internal const int XmaxOffset = 49;

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaFreeHead = 0;
    private const int MetaHwm = 8;
    private const int MetaFormatVersion = 31; // byte (FT-26)

    private static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize; // 143

    private readonly IPagedFile _file;
    private readonly BlobStore _blobs;
    private long _freeHead;
    private long _hwm;

    public PropertyStore(IPagedFile file, IPagedFile blobFile)
    {
        _blobs = new BlobStore(blobFile);
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _freeHead = -1; _hwm = 0;
            FlushMeta(initialise: true);
        }
        else
        {
            CheckFormatVersion();
            LoadMeta();
        }
    }

    public PropertyId Create(PropertyKeyId keyId, in PropertyValue value, PropertyId currentFirst)
    {
        // FT-26 MVCC: 論理削除に伴う slot 非再利用で free list は空のまま hwm 単調増加。
        long id;
        if (_freeHead >= 0)
        {
            id = _freeHead;
            var (fpid, foff) = Location(id);
            using var fh = _file.PinForRead(fpid);
            _freeHead = RecordHelpers.ReadInt48(fh.Data[(foff + 35)..]);
        }
        else
        {
            id = _hwm++;
        }

        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();

        bool spillover = value.Type is PropertyValueType.String or PropertyValueType.Bytes
            && value.EncodedSize > InlineCapacity;

        byte flags = FlagInUse;
        if (spillover) flags |= FlagSpillover;
        rec[0] = flags;
        BinaryPrimitives.WriteInt32LittleEndian(rec[1..], keyId.Value);
        rec[5] = (byte)value.Type;
        RecordHelpers.WriteInt48(rec[35..], currentFirst.Value); // NextPropId = old head
        BinaryPrimitives.WriteInt64LittleEndian(rec[XminOffset..], MvccContext.CurrentTxId.Value);
        BinaryPrimitives.WriteInt64LittleEndian(rec[XmaxOffset..], 0L);

        if (spillover)
        {
            long blobId = _blobs.Write(value.Type == PropertyValueType.String
                ? value.Utf8StringValue
                : value.BytesValue);
            RecordHelpers.WriteInt40(rec[30..], blobId);
        }
        else
        {
            WriteInline(rec[6..], value);
        }

        _file.UnpinDirty(wpid, 0);
        FlushMeta();
        return new PropertyId(id);
    }

    public PropertyId Delete(PropertyId propId, PropertyId currentFirst)
    {
        // FT-26 MVCC: 論理削除のみ — xmax をスタンプ、チェーン unlink / free list 投入 / blob 解放はしない。
        // currentFirst (= chain head) は変更されないのでそのまま返す (snapshot reader が辿れる)。
        // 物理回収 (blob 含む) は vacuum (OP-3) 担当。
        var (pageId, off) = Location(propId.Value);
        var ph = _file.PinForWrite(pageId);
        Span<byte> rec = ph.Data.Slice(off, RecordSize);
        BinaryPrimitives.WriteInt64LittleEndian(rec[XmaxOffset..], MvccContext.CurrentTxId.Value);
        _file.UnpinDirty(pageId, 0);

        return currentFirst;
    }

    public PropertyReadHandle Read(PropertyId propId)
    {
        // FT-30: HWM 超 / 負 ID は "存在しない" 扱い。NodeStore.Read と同じ理由。
        if (propId.Value < 0 || propId.Value >= _hwm)
            return new PropertyReadHandle(propId, default, PropertyId.Invalid, default, inUse: false);
        var (pageId, off) = Location(propId.Value);
        using var h = _file.PinForRead(pageId);
        ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
        var keyId = new PropertyKeyId(BinaryPrimitives.ReadInt32LittleEndian(rec[1..]));
        var vtype = (PropertyValueType)rec[5];
        var nextId = new PropertyId(RecordHelpers.ReadInt48(rec[35..]));
        bool spillover = (rec[0] & FlagSpillover) != 0;
        bool inUse = (rec[0] & FlagInUse) != 0;
        long xmin = BinaryPrimitives.ReadInt64LittleEndian(rec[XminOffset..]);
        long xmax = BinaryPrimitives.ReadInt64LittleEndian(rec[XmaxOffset..]);

        PropertyValue value;
        if (spillover)
        {
            long blobId = RecordHelpers.ReadInt40(rec[30..]);
            long len = _blobs.GetLength(blobId);
            byte[] buf = new byte[len];
            _blobs.Read(blobId, buf);
            value = vtype == PropertyValueType.String
                ? PropertyValue.FromUtf8(buf)
                : PropertyValue.FromBytes(buf);
        }
        else
        {
            value = ReadInline(rec[6..], vtype);
        }

        // FT-26: MVCC visibility をフィルタする。invisible は InUse=false に縮退。
        if (inUse && !Visibility.IsVisibleAmbient(xmin, xmax))
            inUse = false;

        return new PropertyReadHandle(propId, keyId, nextId, value, inUse);
    }

    public PropertyEnumerator Enumerate(PropertyId firstPropId)
        => new(this, firstPropId);

    // --- internal bulk-load helpers ---

    internal PropertyId BulkCreate(int keyId, PropertyValueType type, long scalar, byte[]? data, long nextPropId)
    {
        long id = _hwm++;
        var (wpid, woff) = Location(id);
        EnsurePage(wpid);
        var ph = _file.PinForWrite(wpid);
        Span<byte> rec = ph.Data.Slice(woff, RecordSize);
        rec.Clear();

        bool spillover = type is PropertyValueType.String or PropertyValueType.Bytes
            && (data?.Length ?? 0) > InlineCapacity;

        byte flags = FlagInUse;
        if (spillover) flags |= FlagSpillover;
        rec[0] = flags;
        BinaryPrimitives.WriteInt32LittleEndian(rec[1..], keyId);
        rec[5] = (byte)type;
        RecordHelpers.WriteInt48(rec[35..], nextPropId);
        // FT-26: bulk load は MvccContext が無いので Bootstrap を xmin に。
        BinaryPrimitives.WriteInt64LittleEndian(rec[XminOffset..], TransactionId.Bootstrap.Value);
        BinaryPrimitives.WriteInt64LittleEndian(rec[XmaxOffset..], 0L);

        if (spillover)
        {
            long blobId = _blobs.Write(data!);
            RecordHelpers.WriteInt40(rec[30..], blobId);
        }
        else
        {
            PropertyValue pv = type switch
            {
                PropertyValueType.Bool   => PropertyValue.FromBool(scalar != 0),
                PropertyValueType.Int32  => PropertyValue.FromInt32((int)scalar),
                PropertyValueType.Int64  => PropertyValue.FromInt64(scalar),
                PropertyValueType.Double => PropertyValue.FromDouble(BitConverter.Int64BitsToDouble(scalar)),
                PropertyValueType.String => PropertyValue.FromUtf8((data ?? Array.Empty<byte>()).AsSpan()),
                PropertyValueType.Bytes  => PropertyValue.FromBytes((data ?? Array.Empty<byte>()).AsSpan()),
                _ => throw new Quiver.Core.CorruptionException($"Unknown property type {type}")
            };
            WriteInline(rec[6..], pv);
        }

        _file.UnpinDirty(wpid, 0);
        return new PropertyId(id);
    }

    internal void BulkFlushMeta() => FlushMeta();

    /// <summary>
    /// OP-3 vacuum: ノードストアと協調してプロパティ chain を整理し、dead version を
    /// 物理回収する。実行手順:
    /// <list type="number">
    ///   <item>各ノード slot を raw 走査。</item>
    ///   <item>ノードが reclaim 対象 (xmax committed かつ horizon 未満) なら、その prop chain を
    ///         全 free (blob 含む) しノードの FirstPropId は触らない (ノード vacuum で record ごと消える)。</item>
    ///   <item>ノードが live なら chain を walk し、dead prop を unlink + free。</item>
    /// </list>
    /// </summary>
    /// <remarks>呼び出し前提: アクティブトランザクション 0 件。</remarks>
    /// <returns>物理回収したプロパティ版数。</returns>
    internal int VacuumDeadVersions(NodeStore nodeStore, long horizonTxId, CommittedTxRegistry committed)
    {
        int reclaimed = 0;
        long hwm = nodeStore.Hwm;
        for (long nid = 0; nid < hwm; nid++)
        {
            var raw = nodeStore.ReadRaw(nid);
            if (!raw.InUse) continue;
            if (!raw.FirstPropId.IsValid) continue;

            bool nodeWillBeReclaimed = raw.Xmax != 0
                && raw.Xmax < horizonTxId
                && committed.IsCommitted(raw.Xmax);

            if (nodeWillBeReclaimed)
            {
                // 全 chain を解放。NodeStore 側で FirstPropId はクリアされる。
                reclaimed += ReclaimEntireChain(raw.FirstPropId);
            }
            else
            {
                // 部分解放: dead prop だけ unlink + free。chain head が変わったら NodeStore に書き戻す。
                reclaimed += CompactChain(new NodeId(nid), raw.FirstPropId, nodeStore, horizonTxId, committed);
            }
        }
        if (reclaimed > 0)
        {
            ShrinkHwmFromTrailingFreeSlots();
            FlushMeta();
        }
        return reclaimed;
    }

    private int ReclaimEntireChain(PropertyId head)
    {
        int count = 0;
        var cur = head;
        // chain 長は通常数件程度。万一のループ防止として hwm を上限にする。
        long guard = _hwm + 1;
        while (cur.IsValid && guard-- > 0)
        {
            var next = ReclaimSlot(cur);
            cur = next;
            count++;
        }
        return count;
    }

    private int CompactChain(NodeId owner, PropertyId head, NodeStore nodeStore,
        long horizonTxId, CommittedTxRegistry committed)
    {
        // raw に chain を遍歴して live/dead に分類。
        var entries = new List<(PropertyId Id, PropertyId Next, bool Dead)>();
        var cur = head;
        long guard = _hwm + 1;
        while (cur.IsValid && guard-- > 0)
        {
            var (pageId, off) = Location(cur.Value);
            PropertyId next;
            bool dead;
            using (var h = _file.PinForRead(pageId))
            {
                ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
                bool inUse = (rec[0] & FlagInUse) != 0;
                next = new PropertyId(RecordHelpers.ReadInt48(rec[35..]));
                if (!inUse)
                {
                    // すでに物理 free。chain 上にあれば後段で繋ぎ替え、props は触らない。
                    dead = true;
                }
                else
                {
                    long xmax = BinaryPrimitives.ReadInt64LittleEndian(rec[XmaxOffset..]);
                    dead = xmax != 0 && xmax < horizonTxId && committed.IsCommitted(xmax);
                }
            }
            entries.Add((cur, next, dead));
            cur = next;
        }

        if (entries.Count == 0) return 0;

        // live 順序を保ったまま再リンク。
        PropertyId newHead = PropertyId.Invalid;
        int reclaimedHere = 0;
        // 後ろから組み立て (NextPropId は単方向リンク)。
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var (id, _, dead) = entries[i];
            if (dead)
            {
                // free 操作。raw inUse=1 のみ blob 解放してリクレイム (= 既に物理 free の場合はスキップ)。
                var (pageId, off) = Location(id.Value);
                bool wasInUse;
                {
                    using var h = _file.PinForRead(pageId);
                    wasInUse = (h.Data[off] & FlagInUse) != 0;
                }
                if (wasInUse)
                {
                    ReclaimSlot(id);
                    reclaimedHere++;
                }
                continue;
            }
            // live: rewrite NextPropId → newHead
            var (lpid, loff) = Location(id.Value);
            var ph = _file.PinForWrite(lpid);
            RecordHelpers.WriteInt48(ph.Data[(loff + 35)..], newHead.Value);
            _file.UnpinDirty(lpid, 0);
            newHead = id;
        }

        // node.FirstPropId 更新
        nodeStore.UpdateFirstPropId(owner, newHead);
        return reclaimedHere;
    }

    /// <summary>
    /// 単一プロパティ slot を物理回収する。blob 解放 + 記録クリア + free list 投入。
    /// 戻り値は元レコードの NextPropId (= chain 上で次の prop)。
    /// </summary>
    private PropertyId ReclaimSlot(PropertyId id)
    {
        var (pageId, off) = Location(id.Value);
        PropertyId next;
        long blobId = -1;
        bool spillover;

        {
            using var h = _file.PinForRead(pageId);
            ReadOnlySpan<byte> rec = h.Data.Slice(off, RecordSize);
            byte flags = rec[0];
            spillover = (flags & FlagSpillover) != 0;
            next = new PropertyId(RecordHelpers.ReadInt48(rec[35..]));
            if (spillover) blobId = RecordHelpers.ReadInt40(rec[30..]);
        }

        if (spillover && blobId >= 0)
            _blobs.Free(blobId);

        var ph = _file.PinForWrite(pageId);
        Span<byte> rec2 = ph.Data.Slice(off, RecordSize);
        rec2.Clear();
        // free list ポインタは NextPropId 位置 (offset 35, 48-bit) に書く。
        RecordHelpers.WriteInt48(rec2[35..], _freeHead);
        _file.UnpinDirty(pageId, 0);

        _freeHead = id.Value;
        return next;
    }

    private void ShrinkHwmFromTrailingFreeSlots()
    {
        long oldHwm = _hwm;
        long newHwm = oldHwm;
        while (newHwm > 0)
        {
            long candidate = newHwm - 1;
            var (pageId, off) = Location(candidate);
            using var h = _file.PinForRead(pageId);
            bool inUse = (h.Data[off] & FlagInUse) != 0;
            if (inUse) break;
            newHwm--;
        }
        if (newHwm == oldHwm) return;

        // free list 内の対象 slot を取り除き繋ぎ直す。NextPropId フィールド (offset 35) に
        // 次の free ポインタが入っている。
        long head = _freeHead;
        var keep = new List<long>();
        long guard = oldHwm + 1;
        while (head >= 0 && guard-- > 0)
        {
            if (head < newHwm) keep.Add(head);
            var (pageId, off) = Location(head);
            using var h = _file.PinForRead(pageId);
            long next = RecordHelpers.ReadInt48(h.Data[(off + 35)..]);
            head = next;
        }
        long newHeadFree = -1;
        for (int i = keep.Count - 1; i >= 0; i--)
        {
            long id = keep[i];
            var (pageId, off) = Location(id);
            var ph = _file.PinForWrite(pageId);
            Span<byte> rec = ph.Data.Slice(off, RecordSize);
            RecordHelpers.WriteInt48(rec[35..], newHeadFree);
            _file.UnpinDirty(pageId, 0);
            newHeadFree = id;
        }
        _freeHead = newHeadFree;
        _hwm = newHwm;
    }

    /// <summary>OP-3 / テスト用。現在の HWM スロット数 (free 含む)。</summary>
    internal long Hwm => _hwm;

    /// <summary>OP-3 / テスト用。free list 先頭 (-1 で空)。</summary>
    internal long FreeHead => _freeHead;

    /// <summary>
    /// FT-15: ヘッダページからインメモリのメタ (hwm / freeHead) を読み直す。
    /// 内部の BlobStore のメタも同時に同期する。abort の before-image 巻き戻し後、
    /// およびクラッシュ recovery 後に呼ばれる。
    /// </summary>
    internal void ReloadMeta()
    {
        LoadMeta();
        _blobs.ReloadMeta();
    }

    // --- private ---

    private static void WriteInline(Span<byte> inline, in PropertyValue v)
    {
        switch (v.Type)
        {
            case PropertyValueType.Bool:
                inline[0] = v.BoolValue ? (byte)1 : (byte)0;
                break;
            case PropertyValueType.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(inline, v.Int32Value);
                break;
            case PropertyValueType.Int64:
                BinaryPrimitives.WriteInt64LittleEndian(inline, v.Int64Value);
                break;
            case PropertyValueType.Double:
                BinaryPrimitives.WriteInt64LittleEndian(inline, BitConverter.DoubleToInt64Bits(v.DoubleValue));
                break;
            case PropertyValueType.String:
            {
                var span = v.Utf8StringValue;
                inline[0] = (byte)span.Length;
                span.CopyTo(inline[1..]);
                break;
            }
            case PropertyValueType.Bytes:
            {
                var span = v.BytesValue;
                inline[0] = (byte)span.Length;
                span.CopyTo(inline[1..]);
                break;
            }
        }
    }

    private static PropertyValue ReadInline(ReadOnlySpan<byte> inline, PropertyValueType vtype)
        => vtype switch
        {
            PropertyValueType.Bool => PropertyValue.FromBool(inline[0] != 0),
            PropertyValueType.Int32 => PropertyValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(inline)),
            PropertyValueType.Int64 => PropertyValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(inline)),
            PropertyValueType.Double => PropertyValue.FromDouble(
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(inline))),
            PropertyValueType.String => PropertyValue.FromUtf8(CopyInlineBytes(inline)),
            PropertyValueType.Bytes => PropertyValue.FromBytes(CopyInlineBytes(inline)),
            _ => throw new Quiver.Core.CorruptionException($"Unknown property value type {vtype}"),
        };

    private static byte[] CopyInlineBytes(ReadOnlySpan<byte> inline)
    {
        int len = inline[0];
        var buf = new byte[len];
        inline[1..(1 + len)].CopyTo(buf);
        return buf;
    }

    private (PageId pageId, int offset) Location(long id)
    {
        int rpp = RecordsPerPage;
        return (new PageId(id / rpp + 2), (int)(id % rpp) * RecordSize);
    }

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
            _file.AllocatePage(PageKind.PropertyRecord);
    }

    private void LoadMeta()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _freeHead = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaFreeHead..]);
        _hwm = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaHwm..]);
    }

    private void CheckFormatVersion()
    {
        using var h = _file.PinForRead(HeaderPageId);
        byte v = h.Data[MetaFormatVersion];
        if (v != FormatVersion.V2Mvcc)
            throw new FormatVersionMismatchException("props", v, FormatVersion.V2Mvcc);
    }

    private void FlushMeta(bool initialise = false)
    {
        var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaFreeHead..], _freeHead);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaHwm..], _hwm);
        if (initialise)
            ph.Data[MetaFormatVersion] = FormatVersion.V2Mvcc;
        _file.UnpinDirty(HeaderPageId, 0);
    }
}
