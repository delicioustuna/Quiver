namespace Quiver.Wal;

public enum WalRecordType : byte
{
    Begin = 1,
    Commit = 2,
    Abort = 3,
    PageImage = 10,
    PageDelta = 11,
    // FT-15: あるページがトランザクション内で初めて変更される直前の内容 (before-image)。
    // クラッシュ recovery の undo パスが、コミットも abort もしていないトランザクションの
    // 変更を巻き戻すために使う。ペイロード形式は PageImage と共通 (WalPageImageCodec)。
    CompensationLogRecord = 12,
    Checkpoint = 100,
    EndOfSegment = 0xFE,
}
