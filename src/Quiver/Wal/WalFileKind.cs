namespace Quiver.Storage.Wal;

internal enum WalFileKind : byte
{
    Nodes         = 1,
    Relationships = 2,
    Properties    = 3,
    BlobData      = 4,
    /// <summary>NodeStore に対応する MVCC+SSN メタデータ sidecar。</summary>
    NodeVersionMeta = 5,
    /// <summary>RelationshipStore に対応する MVCC+SSN メタデータ sidecar。</summary>
    RelationshipVersionMeta = 6,
    /// <summary>PropertyStore に対応する MVCC+SSN メタデータ sidecar。</summary>
    PropertyVersionMeta = 7,
}
