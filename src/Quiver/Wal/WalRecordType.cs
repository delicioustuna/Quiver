namespace Quiver.Storage.Wal;

internal enum WalRecordType : byte
{
    Begin = 1,
    Commit = 2,
    Abort = 3,
    PageImage = 10,
    PageDelta = 11,
    // あるページがトランザクション内で初めて変更される直前の内容 (before-image)。
    // クラッシュ recovery の undo パスが、コミットも abort もしていないトランザクションの
    // 変更を巻き戻すために使う。ペイロード形式は PageImage と共通 (WalPageImageCodec)。
    CompensationLogRecord = 12,
    // B+Tree インデックスへの 1 件の論理ミューテーション (Insert / Delete)。
    // 索引ファイルは WAL ページロギング対象外なので、abort / crash でエントリを
    // 巻き戻すために論理 undo レコードを別途持つ。ペイロードは IndexMutationCodec。
    // 索引が page-WAL 化され予約値として残置 (新規には現れない)。
    IndexMutation = 13,
    // postings/norms B+Tree leaf への state-setting 論理ミューテーション
    // (Upsert key=value / Delete key)。leaf 更新のページイメージ (CLR + PageImage) を本レコードへ
    // 置換し取込 WAL 増幅を圧縮する。SMO (split/merge) は従来の page-WAL を維持。
    // 冪等 state-setting で、redo は再実行 (Pass 2b)、undo は逆操作。ペイロードは FtLeafMutationCodec。
    FtLeafMutation = 17,
    // postings/norms B+Tree の SMO (split/merge/root 変更) で書き換わった構造ページの
    // after-image。nested top action として **commit/abort を問わず無条件に redo し、
    // 決して undo しない**。ペイロード形式は PageImage と共通 (WalPageImageCodec)。recovery の
    // presume-committed 推定には一切寄与させない (PageImage のみが FlushPending マーカ)。
    FtStructureImage = 18,
    // チェックポイント atomicity の Begin/End sentinel。
    // CheckpointBegin はチェックポイント開始 (dirty page flush 前)、
    // CheckpointEnd は全 page + index fsync 完了後に書く。recovery は
    // 「Begin と対になる End を持つチェックポイント」だけを「完了済み」と認識し、
    // Begin のみ (= 途中で kill された partial checkpoint) は無視して redo 起点を
    // 前回 End まで戻す。これにより checkpoint 途中 kill でも data file が partial
    // 状態のまま WAL truncate が走ったような偽の「完了」を検出できない事態を防ぐ。
    // ペイロード形式は WriteAheadLog.WriteCheckpointBegin / WriteCheckpointEnd を参照。
    CheckpointBegin = 14,
    CheckpointEnd = 15,
    // OP-5: Vacuum がページファイル末尾を物理 truncate した直後に書く。recovery の
    // Pass 2 redo で再適用される (= 冪等)。ペイロードは [fileKind:1][newPageCount:8]。
    // WAL 順序の不変条件: 同一 fileKind に対する後続の AllocatePage は newPageCount より
    // 大きな PageId を生成しうるので、recovery 側は LSN 順に処理することで「truncate →
    // 拡張」を再現する (truncate より後の WAL record が再びファイルを必要なサイズへ拡張)。
    FileTruncate = 16,
    // 旧型式。「page fsync → log fsync → truncate」を 1 レコードで表現していた。
    // 既存 DB との互換のため recovery 側で読み飛ばし起点として認識する。新規には書かない。
    Checkpoint = 100,
    EndOfSegment = 0xFE,
}
