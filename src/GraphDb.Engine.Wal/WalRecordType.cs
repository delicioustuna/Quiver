namespace GraphDb.Engine.Wal;

public enum WalRecordType : byte
{
    Begin = 1,
    Commit = 2,
    Abort = 3,
    PageImage = 10,
    PageDelta = 11,
    Checkpoint = 100,
    EndOfSegment = 0xFE,
}
