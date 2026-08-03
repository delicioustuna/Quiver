# 04. リカバリとトラブルシュート

> **いつ読むか** — クラッシュ後に DB が起動できない、データが想定と合わない、索引が壊れて
> いる気がする、ログに警告が出ている、というとき。まず「Quiver の復旧は何を保証するか」を
> 押さえてから、症状別の対処に進む。

---

## 前提: クラッシュ復旧が保証すること

Quiver は QUIVER-SW family の page-image WAL で durability を担保する。

- **明示的な `Commit` が WAL へ永続化されたデータは、プロセスを強制終了しても再 open で復元される。**
- **commit record を持たない transaction の `PageImage` は再生しない。**
- 索引を含む B+Tree も同じ page WAL の対象なので、データと索引を同じ commit 境界で復旧する。
- checkpoint は対応する `CheckpointBegin` と `CheckpointEnd` が揃った場合だけ採用する。
- 旧 DB、旧 WAL、未知header extension、unknown record、truncation、checksum corruption は fail-fast で拒否する。

つまり「再オープンすれば、最後に成功した commit の直後の整合状態に戻る」のが基本契約。
再オープンは特別な操作ではなく、ただ `QuiverDatabase.Open(dir)` を呼ぶだけで recovery が自動で走る。

```csharp
using var db = QuiverDatabase.Open(dir);   // ← ここで WAL replay (recovery) が実行される
```

復旧が走った回数は `dotnet-counters` の `crash-recovery-count` で観測できる。通常運用では 0。
非ゼロが増えていれば、どこかでプロセスが異常終了している兆候。

---

## 症状別トラブルシュート

### A. 再オープンに時間がかかる

**原因**: 前回 checkpoint 以降の WAL が大きく、replay する量が多い。

**対処**:

- `CheckpointThresholdBytes` を下げる、または `CheckpointPolicy = Adaptive` + `TargetRecoveryTime` で
  recovery 時間を目標値に抑える ([03_performance_tuning.md](03_performance_tuning.md))。
- 正常終了 (`Dispose`) を必ず通す運用にする。正常終了時はクリーンな状態になり次回起動が軽い。
- per-tx の大量書き込みで WAL が膨れていないか確認 (鉄則違反)。

### B. 起動時に `CorruptionException` / チェックサム不一致

**原因**: ページの torn write、ストレージの bit corruption、またはバックアップの不完全コピー
(開いたままコピーした等)。

**対処**:

1. **元のディレクトリを退避** (`graph` → `graph.broken`)。上書きしない。
2. 直近の正常なバックアップから復元する ([02_backup_restore.md](02_backup_restore.md))。
3. バックアップが無い場合、`graph.broken` を保全したまま開発側に WAL とエラーを共有して調査。
4. 再発する場合はストレージ (ディスク / ボリューム) の健全性を疑う。`EnableChecksums = true` を
   維持していれば腐敗を早期に検出できているということ — 無効化しないこと。

> 読み取り API は HWM (high-water mark) 安全化済み。範囲外/負の ID を `VertexExists` /
> `HasProperty` に渡しても `CorruptionException` ではなく安全に false 返しになる。
> 起動時の `CorruptionException` は「データファイル自体の破損」であり、これとは別物。

### C. `StorageFormatMismatchException` / `WalFormatMismatchException` で開けない

**原因**: QUIVER-SW family version 2 ではない DB または WAL を開こうとしている。

**対処**:

1. DB を開いている全プロセスを停止する。
2. `QuiverDatabase.UpgradeStorage(path)` を `Open` より前に明示的に呼ぶ。
3. `StorageUpgradeNotSupportedException` なら、対応する旧 build の論理 export または元データから
   現行 DB を新規構築する。
4. 旧ファイルの magic や version を書き換えない。
5. WAL だけを削除して起動しない。

```csharp
StorageUpgradeResult result = QuiverDatabase.UpgradeStorage(path);
Console.WriteLine($"storage={result.Status}, {result.SourceVersion} -> {result.TargetVersion}");

using var db = QuiverDatabase.Open(path);
```

現行 family version 2 なら `AlreadyCurrent` となり、database file は変更されない。
実変換が成功した版では、既定で `<source>.pre-upgrade-v<version>.bak` に移行前 file を保持する。
切替途中の marker が残った場合は、次の `UpgradeStorage` が完成済み target の設置または source の復元を行う。
`Open` は移行を暗黙実行しない。

### D. データは戻ったのに索引検索の結果がおかしい / 余分なヒットがある

**原因**: abort や crash の後で索引に orphan entry が残っている可能性がある。
ID が再利用されると orphan が別の Vertex を指し、検索で偽ヒットになる。

**検出** — `CheckIndexConsistency()`:

```csharp
var report = db.Diagnostics.CheckIndexConsistency();
Console.WriteLine($"索引数={report.IndexCount}, エントリ数={report.EntryCount}, " +
                  $"orphan={report.OrphanCount}, labelIndex orphan={report.LabelIndexOrphanCount}");

foreach (var o in report.Orphans)
    Console.WriteLine($"  {o.IndexName}: entityId={o.EntityId}");
```

**修復** — `RepairIndexes()`:

```csharp
// まず DryRun で「何を消すか」を確認 (ファイルは触らない)
var dry = db.Diagnostics.RepairIndexes(IndexRepairMode.DryRun);

// 問題なければ Apply で実削除
var applied = db.Diagnostics.RepairIndexes(IndexRepairMode.Apply);
Console.WriteLine($"除去した orphan = {applied.RemovedCount}, " +
                  $"labelIndex 再構築 = {applied.LabelIndexInvalidated}");
```

- `Apply` は B+Tree の orphan を生キー削除し、`LabelVertexIndex` は orphan があれば invalidate して
  次回 lookup で再構築する。
- **起動時に自動修復したい** 場合は `QuiverDatabaseOptions.AutoRepairOrphansOnRecovery = true` を設定すると、
  open 完了直後に `RepairIndexes(Apply)` が自動実行される (既定 false)。常に整合を優先したい運用向け。

### E. ディスク使用量が想定より大きい / 削除したのに減らない

**原因**: MVCC では削除/更新は即座に物理削除せず dead version として残す。これを物理回収するのが
**vacuum**。

**対処** — `Vacuum()`:

```csharp
// まず DryRun でどれだけ回収できるか見る
var dry = db.Vacuum(new VacuumOptions { Mode = VacuumMode.DryRun });
Console.WriteLine($"回収可能 Vertex version 数 = {dry.ReclaimedVertices}, horizon={dry.HorizonTxId}");

// 実行。active reader がいても最古 snapshot の horizon より前は回収できる。
var report = db.Vacuum();
if (report.Skipped)
    Console.WriteLine("この backend は vacuum を実行しませんでした");
else
    Console.WriteLine($"回収={report.ReclaimedVertices} 版, truncate={report.TruncatedPages} ページ, " +
                      $"{report.ElapsedMs}ms");
```

- vacuum は writer lease を取得するが、active reader の終了を待たない。
  最古 snapshot の visibility horizon より前だけを回収し、reader が参照できる version は残す。
- `GetSnapshotDiagnostics()` の `OldestAge` が長い場合は、不要な read transaction が開いたままになっていないか確認する。
- 物理 truncate (ページファイル縮小) にも対応済み (`TruncatedPages` に削減ページ数が出る)。
- 自動で回したい場合は `QuiverDatabaseOptions.AutoVacuum = true` と `AutoVacuumInterval` を設定する。

### F. 「DB が開けない / 既にロックされている」

**原因**: Quiver は **単一プロセス embedded** 前提。同じディレクトリを 2 つのプロセス
(または同一プロセス内で 2 回 `Open`) から同時に開くことはできない。

**対処**:

- 前のプロセスが本当に終了しているか確認 (ゾンビプロセス、テストの後始末漏れ)。
- 1 プロセス内では `QuiverDatabase` を **singleton** として共有する (DI なら `AddQuiver` が singleton 登録)。
  複数スレッドからの同時アクセスは `QuiverDatabase` インスタンスを共有すれば安全。

---

## ログの読み方

core は `Quiver-EventSource` から transaction、query、checkpoint、WAL flush の構造化イベントを出す。
`Quiver.Hosting` 利用時はこれらがホストの `ILoggerFactory` へ自動転送される。
Hosting を使わない場合は `dotnet-trace` または独自の `EventListener` で購読する。

- **起動時**: recovery が再生した LSN 範囲と winner transaction を確認する。
  毎回 recovery が走る (= 直前に異常終了している) なら正常終了経路を見直す。
- **writer lease 待ち**: `writer-contention-count` と `writer-wait-duration-ms` を確認する。
  競合が続く場合は mutation を一つの writer queue へ集約し、トランザクションをまとめる。
- **長時間 reader**: `active-snapshot-count` と `oldest-snapshot-age-seconds` を確認する。
  `db.Diagnostics.GetSnapshotDiagnostics()` から最古 snapshot の開始位置と high-water も取得できる。
- **checkpoint**: 頻度が高すぎ/低すぎなら threshold を調整。

ライブ観測は `dotnet-counters ... --counters Quiver-EventSource` ([docs/cookbook.md](../cookbook.md) §9)。
`crash-recovery-count` / `index-orphan-count` / `writer-contention-count` /
`oldest-snapshot-age-seconds` / `vacuum-progress-percent` が
トラブルシュートの主要シグナル。

---

## 起動時ヘルスチェックを組み込む

トラブルを「起きてから気づく」のではなく、起動のたびに自動で整合を確認しておくと早期発見できる。
アプリ起動シーケンスに以下のような軽量ヘルスチェックを挟むのが有効:

```csharp
static void StartupHealthCheck(QuiverDatabase db, ILogger log)
{
    var stats = db.Diagnostics.GetStatistics();
    log.LogInformation("起動: vertices={Vertices}, edges={Edges}", stats.VertexCount, stats.EdgeCount);

    // 索引整合 (重い場合は起動時ではなく定期ジョブに回す)
    var idx = db.Diagnostics.CheckIndexConsistency();
    if (idx.OrphanCount > 0 || idx.LabelIndexOrphanCount > 0)
    {
        log.LogWarning("索引 orphan 検出: btree={Btree}, label={Label} — RepairIndexes を検討",
            idx.OrphanCount, idx.LabelIndexOrphanCount);
        // 自動修復したいなら RepairIndexes(Apply)、または起動オプションで
        // AutoRepairOrphansOnRecovery = true を設定しておく
    }
}
```

`CheckIndexConsistency` は全索引を走査するため、巨大 DB では起動時ではなく低トラフィック時間帯の
定期ジョブに回す。常時整合を最優先する運用なら `AutoRepairOrphansOnRecovery = true` で
open 直後に自動修復させる。

## 復旧の一通り (ワークフロー例)

「本番でプロセスが落ち、再起動したらおかしい」というケースの一連の流れ:

1. **退避** — `graph` を `graph.incident-20260530` にリネームしてオリジナルを保全。
   調査の証拠を消さないことが最優先。
2. **再オープンを試す** — 別パスでなく退避コピーに対して `QuiverDatabase.Open` を試し、
   recovery log の replay LSN 範囲を確認する。
3. **開けた場合**:
   - `GetStatistics()` で件数が想定どおりか確認。
   - `CheckIndexConsistency()` → orphan があれば `RepairIndexes(DryRun)` で内容確認 → `Apply`。
   - 問題なければそのまま本番復帰。recovery が走った原因 (異常終了経路) を別途調査。
4. **開けない場合 (`CorruptionException`、format mismatch 等)**:
   - 直近バックアップから復元 ([02_backup_restore.md](02_backup_restore.md))。
   - バックアップが無ければ退避コピー + WAL + エラーを保全して調査依頼。
5. **再発防止** — `crash-recovery-count` の増加を監視に載せ、正常終了 (`Dispose`) を必ず通す
   シャットダウン手順を整備する。

## 困ったときの初動 (チェックリスト)

1. **退避が先**: 壊れたディレクトリを消さずにリネームして保全する。
2. 直近バックアップから復元できるか確認 ([02_backup_restore.md](02_backup_restore.md))。
3. `CheckIndexConsistency()` で索引整合を確認、必要なら `RepairIndexes(Apply)`。
4. ディスク使用量問題なら `Vacuum(DryRun)` → `Vacuum()`。
5. Hosting の構造化ログまたは `dotnet-trace` と `dotnet-counters` で recovery 回数、orphan、writer 待機、snapshot age を観測。
6. `EnableChecksums` は無効化しない。破損の早期検出を失うだけ。
