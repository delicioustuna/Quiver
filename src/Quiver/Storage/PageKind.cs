namespace Quiver.Storage;

internal enum PageKind : byte
{
    Free = 0,
    VertexRecord = 1,
    EdgeRecord = 2,
    PropertyRecord = 3,
    BTreeInternal = 4,
    BTreeLeaf = 5,
    TokenRecord = 6,
    AdjacencyBlock = 7,
    // 単一ファイルコンテナのカタログ root / テナント page-table ページ。
    Catalog = 8,
    PageTable = 9,
    // 版チェーン付き可変長レコードの slotted ヒープ / ItemPointerMap エントリページ。
    SlottedHeap = 10,
    ItemPointerMap = 11,
    // 固定長 slot を sequence 直引きで密配置する incidence ページ。
    IncidenceRecord = 12,
    // edge delta store の append-only ページ。
    EdgeDeltaRecord = 13,
    Header = 0xFF,
}
