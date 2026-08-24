using System.Buffers.Binary;

namespace Yatagarasu.Storage;

/// <summary>
/// 可変長レコードを格納する slotted ページのビュー。
/// 8KB ページ本体 (<see cref="PageHeader"/> 後の <see cref="PageWriteHandle.Data"/> Span) に
/// 対して直接読み書きする ref struct。版チェーン付きレコード (<c>VersionedRecordHeap</c>) と
/// 可変長 overflow ページの両方の土台になる。
///
/// 本体レイアウト:
/// <code>
///   [0]   slotCount : u16        — slot directory のエントリ数 (tombstone 含む)
///   [2]   heapTop   : u16        — レコードヒープ最上端 offset (レコードは [heapTop, body.Length) を占有)
///   [4..] slot dir  : (off:u16,len:u16)[] — 前方へ伸長。off==0 は tombstone (削除済)
///   …free…
///   [heapTop, body.Length)       — レコードデータ。本体末尾から前方へ伸長。
/// </code>
///
/// slot 番号は安定 (compaction はレコードデータのみ移動し slot index は保持)。off==0 を
/// tombstone sentinel に使えるのは、live レコードの offset が必ず slot directory 終端
/// (≥ <see cref="SlotDirStart"/>) 以上で 0 にならないため。
/// </summary>
internal ref struct SlottedPage
{
    private const int HdrSlotCount = 0;   // u16
    private const int HdrHeapTop = 2;     // u16
    public const int SlotDirStart = 4;    // slot directory 開始 offset
    public const int SlotEntrySize = 4;   // off:u16 + len:u16

    private readonly Span<byte> _body;

    public SlottedPage(Span<byte> body) => _body = body;

    public int SlotCount => BinaryPrimitives.ReadUInt16LittleEndian(_body[HdrSlotCount..]);
    private int HeapTop => BinaryPrimitives.ReadUInt16LittleEndian(_body[HdrHeapTop..]);

    private void SetSlotCount(int v) => BinaryPrimitives.WriteUInt16LittleEndian(_body[HdrSlotCount..], (ushort)v);
    private void SetHeapTop(int v) => BinaryPrimitives.WriteUInt16LittleEndian(_body[HdrHeapTop..], (ushort)v);

    /// <summary>新規ページを slotted ページとして初期化する (slotCount=0, heapTop=本体末尾)。</summary>
    public void Init()
    {
        SetSlotCount(0);
        SetHeapTop(_body.Length);
    }

    /// <summary>compaction 無しで連続して取れる空き領域 (slot directory 終端 〜 heapTop)。</summary>
    public int ContiguousFree => HeapTop - (SlotDirStart + SlotCount * SlotEntrySize);

    /// <summary>compaction 後に取れる総空き領域 (tombstone と断片を回収した上限)。</summary>
    public int TotalFree => _body.Length - (SlotDirStart + SlotCount * SlotEntrySize) - LiveRecordBytes();

    /// <summary>
    /// レコードを挿入する。tombstone slot があれば再利用し directory を伸ばさない。
    /// 連続空きが足りなければ compaction を試みる。容量不足なら false。
    /// </summary>
    public bool TryInsert(ReadOnlySpan<byte> record, out int slot)
    {
        slot = -1;
        int len = record.Length;
        int reuse = FindTombstone();
        int dirGrow = reuse >= 0 ? 0 : SlotEntrySize;

        if (ContiguousFree < len + dirGrow)
        {
            // tombstone slot 自体は dir を伸ばさないが、新規 slot は伸ばす。compaction 後に再判定。
            if (TotalFree < len + dirGrow) return false;
            Compact();
            if (ContiguousFree < len + dirGrow) return false;
        }

        int newTop = HeapTop - len;
        record.CopyTo(_body.Slice(newTop, len));
        SetHeapTop(newTop);

        if (reuse >= 0)
        {
            slot = reuse;
            WriteSlot(slot, newTop, len);
        }
        else
        {
            slot = SlotCount;
            WriteSlot(slot, newTop, len);
            SetSlotCount(slot + 1);
        }
        return true;
    }

    /// <summary>slot のレコードバイト列を取得する。tombstone / 範囲外は false。</summary>
    public bool TryGet(int slot, out ReadOnlySpan<byte> record)
    {
        record = default;
        if ((uint)slot >= (uint)SlotCount) return false;
        var (off, len) = ReadSlot(slot);
        if (off == 0) return false;
        record = _body.Slice(off, len);
        return true;
    }

    /// <summary>
    /// slot のレコードを書き込み可能 Span で取得する。同サイズの in-place フィールド更新
    /// (版チェーンの xmax / nextPtr スタンプ等) に使う。tombstone / 範囲外は false。
    /// </summary>
    public bool TryGetMutable(int slot, out Span<byte> record)
    {
        record = default;
        if ((uint)slot >= (uint)SlotCount) return false;
        var (off, len) = ReadSlot(slot);
        if (off == 0) return false;
        record = _body.Slice(off, len);
        return true;
    }

    /// <summary>
    /// slot のレコードを更新する。新サイズが旧サイズ以下なら in-place、超過なら
    /// 旧バイトを回収扱いにして再配置する。容量不足 (旧バイト回収込みでも入らない) なら
    /// 元の状態を保ったまま false を返す。
    /// </summary>
    public bool TryUpdate(int slot, ReadOnlySpan<byte> record)
    {
        if ((uint)slot >= (uint)SlotCount) return false;
        var (off, len) = ReadSlot(slot);
        if (off == 0) return false;

        if (record.Length <= len)
        {
            record.CopyTo(_body.Slice(off, record.Length));
            WriteSlot(slot, off, record.Length);
            return true;
        }

        // 旧レコードのバイトは回収可能なので空き計算に含める。入らなければ無変更で false。
        if (TotalFree + len < record.Length) return false;

        WriteSlot(slot, 0, 0); // 旧 slot を tombstone (バイトは compaction で回収)
        if (ContiguousFree < record.Length) Compact();
        int newTop = HeapTop - record.Length;
        record.CopyTo(_body.Slice(newTop, record.Length));
        SetHeapTop(newTop);
        WriteSlot(slot, newTop, record.Length);
        return true;
    }

    /// <summary>
    /// live スロット (off != 0) が 1 つも無いか。vacuum が空になった heap
    /// ページを free-page list へ回収する判定に使う (全 version が tombstone 済みのページ)。
    /// </summary>
    public bool HasNoLiveSlots()
    {
        int sc = SlotCount;
        for (int i = 0; i < sc; i++)
            if (ReadSlot(i).off != 0) return false;
        return true;
    }

    /// <summary>slot を tombstone する。バイトの物理回収は <see cref="Compact"/> まで遅延。</summary>
    public bool Delete(int slot)
    {
        if ((uint)slot >= (uint)SlotCount) return false;
        var (off, _) = ReadSlot(slot);
        if (off == 0) return false;
        WriteSlot(slot, 0, 0);
        return true;
    }

    /// <summary>
    /// live レコードを本体末尾へ詰め直し、tombstone と断片を回収する。slot index は保持。
    /// </summary>
    public void Compact()
    {
        int sc = SlotCount;
        Span<byte> tmp = stackalloc byte[_body.Length];
        int writeTop = _body.Length;
        for (int i = 0; i < sc; i++)
        {
            var (off, len) = ReadSlot(i);
            if (off == 0) continue;
            writeTop -= len;
            _body.Slice(off, len).CopyTo(tmp.Slice(writeTop, len)); // 旧位置から退避
            WriteSlot(i, writeTop, len);                            // slot を新 offset へ
        }
        tmp[writeTop..].CopyTo(_body[writeTop..]); // 詰め直した領域を書き戻し
        SetHeapTop(writeTop);
    }

    private int LiveRecordBytes()
    {
        int sum = 0;
        int sc = SlotCount;
        for (int i = 0; i < sc; i++)
        {
            var (off, len) = ReadSlot(i);
            if (off != 0) sum += len;
        }
        return sum;
    }

    private int FindTombstone()
    {
        int sc = SlotCount;
        for (int i = 0; i < sc; i++)
            if (ReadSlot(i).off == 0) return i;
        return -1;
    }

    private (int off, int len) ReadSlot(int i)
    {
        int p = SlotDirStart + i * SlotEntrySize;
        return (BinaryPrimitives.ReadUInt16LittleEndian(_body[p..]),
                BinaryPrimitives.ReadUInt16LittleEndian(_body[(p + 2)..]));
    }

    private void WriteSlot(int i, int off, int len)
    {
        int p = SlotDirStart + i * SlotEntrySize;
        BinaryPrimitives.WriteUInt16LittleEndian(_body[p..], (ushort)off);
        BinaryPrimitives.WriteUInt16LittleEndian(_body[(p + 2)..], (ushort)len);
    }
}

/// <summary>
/// <see cref="SlottedPage"/> の読み取り専用ビュー。<see cref="PageReadHandle.Data"/>
/// (ReadOnlySpan) からレコードを引くために用いる。レイアウトは <see cref="SlottedPage"/> と同一。
/// </summary>
internal readonly ref struct ReadOnlySlottedPage
{
    private const int HdrSlotCount = 0;

    private readonly ReadOnlySpan<byte> _body;

    public ReadOnlySlottedPage(ReadOnlySpan<byte> body) => _body = body;

    public int SlotCount => BinaryPrimitives.ReadUInt16LittleEndian(_body[HdrSlotCount..]);

    /// <summary>slot のレコードバイト列を取得する。tombstone / 範囲外は false。</summary>
    public bool TryGet(int slot, out ReadOnlySpan<byte> record)
    {
        record = default;
        if ((uint)slot >= (uint)SlotCount) return false;
        int p = SlottedPage.SlotDirStart + slot * SlottedPage.SlotEntrySize;
        int off = BinaryPrimitives.ReadUInt16LittleEndian(_body[p..]);
        int len = BinaryPrimitives.ReadUInt16LittleEndian(_body[(p + 2)..]);
        if (off == 0) return false;
        record = _body.Slice(off, len);
        return true;
    }

    /// <summary>live スロット (off != 0) が 1 つも無いか。free-page 判定 (読取側)。</summary>
    public bool HasNoLiveSlots()
    {
        int sc = SlotCount;
        for (int i = 0; i < sc; i++)
        {
            int p = SlottedPage.SlotDirStart + i * SlottedPage.SlotEntrySize;
            if (BinaryPrimitives.ReadUInt16LittleEndian(_body[p..]) != 0) return false;
        }
        return true;
    }
}
