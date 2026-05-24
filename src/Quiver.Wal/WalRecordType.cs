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
    // FT-17: B+Tree インデックスへの 1 件の論理ミューテーション (Insert / Delete)。
    // 索引ファイルは WAL ページロギング対象外なので、abort / crash でエントリを
    // 巻き戻すために論理 undo レコードを別途持つ。ペイロードは IndexMutationCodec。
    IndexMutation = 13,
    // FT-21: チェックポイント atomicity の Begin/End sentinel。
    // CheckpointBegin はチェックポイント開始 (dirty page flush 前)、
    // CheckpointEnd は全 page + index fsync 完了後に書く。recovery は
    // 「Begin と対になる End を持つチェックポイント」だけを「完了済み」と認識し、
    // Begin のみ (= 途中で kill された partial checkpoint) は無視して redo 起点を
    // 前回 End まで戻す。これにより checkpoint 途中 kill でも data file が partial
    // 状態のまま WAL truncate が走ったような偽の「完了」を検出できない事態を防ぐ。
    // ペイロード形式は WriteAheadLog.WriteCheckpointBegin / WriteCheckpointEnd を参照。
    CheckpointBegin = 14,
    CheckpointEnd = 15,
    // 旧型式 (FT-21 以前)。「page fsync → log fsync → truncate」を 1 レコードで表現していた。
    // 既存 DB との互換のため recovery 側で読み飛ばし起点として認識する。新規には書かない。
    Checkpoint = 100,
    EndOfSegment = 0xFE,
}
