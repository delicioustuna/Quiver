namespace Quiver.Storage.Wal;

internal enum WalFileKind : byte
{
    Nodes         = 1,
    Relationships = 2,
    Properties    = 3,
    BlobData      = 4,
    /// <summary>FT-31: NodeStore に対応する MVCC+SSN メタデータ sidecar。</summary>
    NodeVersionMeta = 5,
    /// <summary>FT-31: RelationshipStore に対応する MVCC+SSN メタデータ sidecar。</summary>
    RelationshipVersionMeta = 6,
    /// <summary>FT-31: PropertyStore に対応する MVCC+SSN メタデータ sidecar。</summary>
    PropertyVersionMeta = 7,
}
