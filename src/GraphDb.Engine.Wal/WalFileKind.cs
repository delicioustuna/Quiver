namespace GraphDb.Engine.Wal;

public enum WalFileKind : byte
{
    Nodes         = 1,
    Relationships = 2,
    Properties    = 3,
    BlobData      = 4,
}
