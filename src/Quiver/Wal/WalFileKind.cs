namespace Quiver.Storage.Wal;

internal enum WalFileKind : byte
{
    Vertices         = 1,
    Edges = 2,
    Properties    = 3,
    BlobData      = 4,
    /// <summary>VertexStore に対応する MVCC+SSN メタデータ sidecar。</summary>
    VertexVersionMeta = 5,
    /// <summary>EdgeStore に対応する MVCC+SSN メタデータ sidecar。</summary>
    EdgeVersionMeta = 6,
    /// <summary>PropertyStore に対応する MVCC+SSN メタデータ sidecar。</summary>
    PropertyVersionMeta = 7,
}
