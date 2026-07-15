# 04. リカバリとトラブルシュート

> **いつ読むか** — クラッシュ後に DB が起動できない、データが想定と合わない、索引が壊れて
> いる気がする、ログに警告が出ている、というとき。まず「Quiver の復旧は何を保証するか」を
> 押さえてから、症状別の対処に進む。

---

## 前提: クラッシュ復旧が保証すること

Quiver は **ARIES ベースの WAL (Write-Ahead Log)** で durability を担保する。

- **commit 済みのデータは、プロセスを `kill` で落としても再オープンで完全に復元される** (redo)。
- **commit していない in-flight な変更は再オープンで取り消される** (補償レコードによる undo / strict rollback)。
- 索引 (B+Tree) も page-WAL の対象なので、データと索引が **同じ時点** に揃って復旧する。
- checkpoint には Begin/End sentinel があり、checkpoint 中にクラッシュしても WAL truncate の
  順序が壊れない。

つまり「再オープンすれば、最後に成功した commit の直後の整合状態に戻る」のが基本契約。
再オープンは特別な操作ではなく、ただ `GraphDatabase.Open(dir)` を呼ぶだけで recovery が自動で走る。

```csharp
using var db = GraphDatabase.Open(dir);   // ← ここで WAL replay (recovery) が実行される
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

**原因**: ページの torn write、ストレージのビット腐敗、あるいはバックアップの不完全コピー
(開いたままコピーした等)。

**対処**:

1. **元のディレクトリを退避** (`graph` → `graph.broken`)。上書きしない。
2. 直近の正常なバックアップから復元する ([02_backup_restore.md](02_backup_restore.md))。
3. バックアップが無い場合、`graph.broken` を保全したまま開発側に WAL とエラーを共有して調査。
4. 再発する場合はストレージ (ディスク / ボリューム) の健全性を疑う。`EnableChecksums = true` を
   維持していれば腐敗を早期に検出できているということ — 無効化しないこと。

> 読み取り API は HWM (high-water mark) 安全化済み。範囲外/負の ID を `NodeExists` /
> `HasProperty` に渡しても `CorruptionException` ではなく安全に false 返しになる。
> 起動時の `CorruptionException` は「データファイル自体の破損」であり、これとは別物。

### C. データは戻ったのに索引検索の結果がおかしい / 余分なヒットがある

**原因**: ごく稀に、abort や crash の後で索引に **orphan エントリ** (解放済みノード ID を指す残骸) が
残ることがある。ID が再利用されると orphan が別ノードを指してしまい、検索で偽ヒットになる。

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

- `Apply` は B+Tree の orphan を生キー削除し、`LabelNodeIndex` は orphan があれば invalidate して
  次回 lookup で再構築する。
- **起動時に自動修復したい** 場合は `GraphDatabaseOptions.AutoRepairOrphansOnRecovery = true` を設定すると、
  open 完了直後に `RepairIndexes(Apply)` が自動実行される (既定 false)。常に整合を優先したい運用向け。

### D. ディスク使用量が想定より大きい / 削除したのに減らない

**原因**: MVCC では削除/更新は即座に物理削除せず dead version として残す。これを物理回収するのが
**vacuum**。

**対処** — `Vacuum()`:

```csharp
// まず DryRun でどれだけ回収できるか見る
var dry = db.Vacuum(new VacuumOptions { Mode = VacuumMode.DryRun });
Console.WriteLine($"回収可能ノード版数 = {dry.ReclaimedNodes}, horizon={dry.HorizonTxId}");

// 実行 (アクティブ tx があるときは安全側で Skipped=true になり何もしない)
var report = db.Vacuum();
if (report.Skipped)
    Console.WriteLine("アクティブ tx があるため vacuum をスキップしました");
else
    Console.WriteLine($"回収={report.ReclaimedNodes} 版, truncate={report.TruncatedPages} ページ, " +
                      $"{report.ElapsedMs}ms");
```

- vacuum は **アクティブトランザクションが 0 のときだけ** 実行される (古い snapshot がまだ dead version を
  見ているかもしれないため)。`Skipped = true` で返ったら、書き込みが落ち着いたタイミングで再実行する。
- 物理 truncate (ページファイル縮小) にも対応済み (`TruncatedPages` に削減ページ数が出る)。
- 自動で回したい場合は AutoVacuum ワーカー (`VacuumPolicy.Auto`) を有効にする。バックグラウンドで
  周期実行される。

### E. 「DB が開けない / 既にロックされている」

**原因**: Quiver は **単一プロセス embedded** 前提。同じディレクトリを 2 つのプロセス
(または同一プロセス内で 2 回 `Open`) から同時に開くことはできない。

**対処**:

- 前のプロセスが本当に終了しているか確認 (ゾンビプロセス、テストの後始末漏れ)。
- 1 プロセス内では `GraphDatabase` を **singleton** として共有する (DI なら `AddQuiver` が singleton 登録)。
  複数スレッドからの同時アクセスは `GraphDatabase` インスタンスを共有すれば安全。

---

## ログの読み方

core は `Quiver-EventSource` から transaction、query、checkpoint、WAL flush の構造化イベントを出す。
`Quiver.Hosting` 利用時はこれらがホストの `ILoggerFactory` へ自動転送される。
Hosting を使わない場合は `dotnet-trace` または独自の `EventListener` で購読する。

- **起動時**: recovery が「何 LSN から何 LSN まで replay したか」「undo した tx 数」を出す。
  毎回 recovery が走る (= 直前に異常終了している) なら正常終了経路を見直す。
- **lock 待ち / deadlock victim**: 競合が多いなら `LockingMode` / `DeadlockDetectionInterval` を調整。
- **checkpoint**: 頻度が高すぎ/低すぎなら threshold を調整。

ライブ観測は `dotnet-counters ... --counters Quiver-EventSource` ([docs/cookbook.md](../cookbook.md) §9)。
`crash-recovery-count` / `index-orphan-count` / `tx-deadlock-victim-count` / `vacuum-progress-percent` が
トラブルシュートの主要シグナル。

---

## 起動時ヘルスチェックを組み込む

トラブルを「起きてから気づく」のではなく、起動のたびに自動で整合を確認しておくと早期発見できる。
アプリ起動シーケンスに以下のような軽量ヘルスチェックを挟むのが有効:

```csharp
static void StartupHealthCheck(GraphDatabase db, ILogger log)
{
    var stats = db.Diagnostics.GetStatistics();
    log.LogInformation("起動: nodes={Nodes}, rels={Rels}", stats.NodeCount, stats.RelationshipCount);

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
2. **再オープンを試す** — 別パスでなく退避コピーに対して `GraphDatabase.Open` を試し、
   recovery ログ (replay LSN 範囲 / undo した tx 数) を確認する。
3. **開けた場合**:
   - `GetStatistics()` で件数が想定どおりか確認。
   - `CheckIndexConsistency()` → orphan があれば `RepairIndexes(DryRun)` で内容確認 → `Apply`。
   - 問題なければそのまま本番復帰。recovery が走った原因 (異常終了経路) を別途調査。
4. **開けない場合 (`CorruptionException` 等)**:
   - 直近バックアップから復元 ([02_backup_restore.md](02_backup_restore.md))。
   - バックアップが無ければ退避コピー + WAL + エラーを保全して調査依頼。
5. **再発防止** — `crash-recovery-count` の増加を監視に載せ、正常終了 (`Dispose`) を必ず通す
   シャットダウン手順を整備する。

## 困ったときの初動 (チェックリスト)

1. **退避が先**: 壊れたディレクトリを消さずにリネームして保全する。
2. 直近バックアップから復元できるか確認 ([02_backup_restore.md](02_backup_restore.md))。
3. `CheckIndexConsistency()` で索引整合を確認、必要なら `RepairIndexes(Apply)`。
4. ディスク使用量問題なら `Vacuum(DryRun)` → `Vacuum()`。
5. Hosting の構造化ログまたは `dotnet-trace` と `dotnet-counters` で recovery 回数・orphan・deadlock を観測。
6. `EnableChecksums` は無効化しない。破損の早期検出を失うだけ。
