namespace GraphDb.Engine.Storage;

public enum PageKind : byte
{
    Free = 0,
    NodeRecord = 1,
    RelationshipRecord = 2,
    PropertyRecord = 3,
    BTreeInternal = 4,
    BTreeLeaf = 5,
    TokenRecord = 6,
    AdjacencyBlock = 7,
    Header = 0xFF,
}
