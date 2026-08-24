namespace Yatagarasu.Storage;

/// <summary>
/// slotted ヒープ上のレコードの物理位置 (pageId, slot)。
/// 論理 ID (Sequence) → 物理位置の間接層 (<see cref="ItemPointerMap"/>) と版チェーンの
/// <c>nextVersionPtr</c> で用いる。
///
/// <para>8 バイトへの pack: 下位 48bit = pageId / 上位 16bit = slot。
/// page 0/1 は予約 (PagedFile メタ / store ヘッダ) でレコードを持たないため、
/// <see cref="Pack"/> 値 0 を null sentinel に使える (zero-fill されたページ・レコードが
/// 自然に null ポインタになり、別途の初期化が不要)。</para>
/// </summary>
internal readonly record struct ItemPointer(long PageId, int Slot)
{
    private const long PageMask = 0xFFFF_FFFF_FFFFL; // 48bit

    /// <summary>null ポインタ (page 0 = 予約ページ)。</summary>
    public static readonly ItemPointer Null = new(0, 0);

    /// <summary>有効なレコード位置か (レコードは page ≥ 2 にのみ置かれる)。</summary>
    public bool IsNull => PageId <= 1;

    /// <summary>8 バイト long へ pack する。null は 0。</summary>
    public long Pack()
        => IsNull ? 0L : (PageId & PageMask) | ((long)(Slot & 0xFFFF) << 48);

    /// <summary>pack 値から復元する。0 は <see cref="Null"/>。</summary>
    public static ItemPointer Unpack(long packed)
        => packed == 0
            ? Null
            : new ItemPointer(packed & PageMask, (int)((ulong)packed >> 48 & 0xFFFF));
}
