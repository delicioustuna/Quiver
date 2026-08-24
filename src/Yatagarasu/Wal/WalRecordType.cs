namespace Yatagarasu.Storage.Wal;

internal enum WalRecordType : byte
{
    BeginWrite = 1,
    PageImage = 2,
    Commit = 3,
    Abort = 4,
    CheckpointBegin = 5,
    CheckpointEnd = 6,
    FileTruncate = 7,
}
