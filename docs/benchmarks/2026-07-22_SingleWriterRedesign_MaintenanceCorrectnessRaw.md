# Single Writer 再設計 Maintenance Correctness 生出力

- 測定日：2026-07-22（Asia/Tokyo）。
- 測定対象：`83f93df76623e0babe9cc3ac9ed53f710e673a91`。
- machine：NIRVANA。
- OS：Windows 10.0.26200、win-x64。
- logical processors：16。
- .NET SDK：10.0.301。
- .NET runtime：10.0.9。
- configuration：Debug。

## Maintenance correctness

long reader、snapshot horizon、auto-vacuum、relationship slot reuse を同じ commit で検証した。

```powershell
dotnet test tests\Quiver.Tests\Quiver.Tests.csproj --filter 'FullyQualifiedName=Quiver.Tests.SnapshotReaderTests.Long_reader_does_not_drift_across_update_and_delete_and_does_not_block_commit|FullyQualifiedName=Quiver.Tests.VacuumTests.Vacuum_reclaims_versions_older_than_an_active_snapshot|FullyQualifiedName=Quiver.Tests.VacuumTests.Vacuum_defers_versions_visible_to_the_oldest_snapshot|FullyQualifiedName=Quiver.Tests.VacuumTests.Relationship_sequence_reuse_waits_for_the_oldest_snapshot_horizon|FullyQualifiedName=Quiver.Tests.QuiverDatabaseTests.Vacuum_reuses_edge_sequence_with_higher_generation_without_retargeting_stale_ids|FullyQualifiedName=Quiver.Tests.AutoVacuumWorkerTests.QuiverDatabase_enabled_AutoVacuum_reclaims_dead_versions_in_background' --artifacts-path <temporary-artifact-directory> -p:UsedAvaloniaProducts=
```

```text
成功!   -失敗:     0、合格:     6、スキップ:     0、合計:     6、期間: 1 s - Quiver.Tests.dll (net10.0)
```

| contract | 確認結果 | 判定 |
|---|---|---|
| long reader | writer commit と delete の後も開始時点の値と存在状態を保持し、writer commit は 2 秒以内に完了した | PASS |
| safe horizon progress | 削除後に開始した reader を待たず、到達不能な version を 1 件回収した | PASS |
| oldest reader protection | oldest reader から可視な slot は回収せず、reader 終了後の次回 vacuum で 1 件回収した | PASS |
| auto-vacuum | 50 ms tick の worker が dead vertex slot を回収し、5 秒以内に Sequence 0 から 49 のいずれかを再利用した | PASS |
| relationship reuse | oldest reader の生存中は元 Sequence を再利用せず、終了後は同じ Sequence を Generation + 1 で再利用した | PASS |
| stale identity rejection | 旧 full ID と Generation 0 の raw ID は、新しい Edge の property を読み書きせず、削除もしなかった | PASS |

## Solution gate

```powershell
dotnet build Quiver.slnx -v minimal --artifacts-path <temporary-artifact-directory> -p:UsedAvaloniaProducts=
```

```text
ビルドに成功しました。
    0 個の警告
    0 エラー
```

各 test project は同じ build artifact に対して `dotnet test <project> --no-build --artifacts-path <temporary-artifact-directory> -p:UsedAvaloniaProducts=` で実行した。

| test project | passed | failed | skipped |
|---|---:|---:|---:|
| Quiver.Tests | 828 | 0 | 0 |
| Quiver.Backend.Tests | 236 | 0 | 0 |
| Quiver.Client.Tests | 216 | 0 | 0 |
| Quiver.Codec.Tests | 7 | 0 | 0 |
| Quiver.FuzzTests | 32 | 0 | 0 |
| Quiver.Hosting.Tests | 21 | 0 | 0 |
| Quiver.Index.Tests | 20 | 0 | 0 |
| Quiver.Operators.Tests | 316 | 0 | 0 |
| Quiver.PropertyTests | 20 | 0 | 0 |
| Quiver.PublicApi.Tests | 1 | 0 | 0 |
| Quiver.Rag.Tests | 64 | 0 | 0 |
| Quiver.SourceGen.Tests | 17 | 0 | 0 |
| Quiver.Storage.Tests | 33 | 0 | 0 |
| Quiver.Stores.Tests | 124 | 0 | 0 |
| Quiver.Transactions.Tests | 67 | 0 | 0 |
| Quiver.Wal.Tests | 27 | 0 | 0 |
| 合計 | 2,029 | 0 | 0 |

## 判定

long reader の snapshot result は不変であり、vacuum は reader を待たず安全な horizon まで進んだ。

reader 終了後の次回 vacuum は回収と relationship Sequence reuse を進め、旧 ID を not found とし、新 ID の Generation を増加させた。

correctness gate は全項目 PASS とする。
