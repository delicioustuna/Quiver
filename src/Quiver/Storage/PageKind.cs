namespace Quiver.Storage;

internal enum PageKind : byte
{
    Free = 0,
    NodeRecord = 1,
    RelationshipRecord = 2,
    PropertyRecord = 3,
    BTreeInternal = 4,
    BTreeLeaf = 5,
    TokenRecord = 6,
    AdjacencyBlock = 7,
    // ARCH-4: 単一ファイルコンテナのカタログ root / テナント page-table ページ。
    Catalog = 8,
    PageTable = 9,
    Header = 0xFF,
}
