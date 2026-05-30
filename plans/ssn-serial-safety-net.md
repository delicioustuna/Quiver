# FT-31 〜 FT-34: SSN (Serial Safety Net) Serializable Isolation の分割実装

## Context

SSN 論文 (Wang et al., DaMoN'15) のアイデアを Quiver に組み込む。当初は単一 FT-31 として
EntityVersionStore sidecar 再構成 + SSN 全実装をまとめる予定だったが、scope が大きく
1 セッションでの高品質な commit が困難なため、**4 つの独立タスク** に分割する。

各タスクは:
- **独立して build green** を維持
- **既存全テスト緑** を維持 (機能変化のあるタスクのみ新規テストを追加)
- 後続タスクを未着手のまま中断しても品質が下がらない単位

タスク間依存:
```
FT-31 (foundation) → FT-32 (record migration) → FT-33 (SSN protocol) → FT-34 (tests+bench)
                  ↘─────────────────────────────────────────────────↗
```
FT-33 は FT-32 後に着手可能 (record から Xmin/Xmax が抜けて sidecar 経由になっているため)。
FT-34 は FT-33 完了後にすべての検証 + bench を回す。

---

# FT-31: EntityVersionStore foundation (sidecar 基盤、機能変化なし)

## 目的

3 種の sidecar PagedFile (`NodeVersionMeta` / `RelationshipVersionMeta` / `PropertyVersionMeta`)
を作り、`IEntityVersionStore` API を整備する。**この時点では sidecar は使われない** —
record の Xmin/Xmax は据え置き、既存 visibility ロジックも変更しない。

## Scope

含む:
- 3 つの新 FileKind を `FileKindCatalog` に登録
- `EntityVersionMeta` struct (Xmin / Xmax / Pstamp / Sstamp、32B)
- `IEntityVersionStore` interface + per-EntityKind 実装 (`NodeVersionStore` / `RelationshipVersionStore` / `PropertyVersionStore`)
- 既存 PagedFile / EnableWalLogging (FT-19) パターンの再利用
- 空のテスト 1〜2 件 (Open → Read default → Write → Read 値検証)

含まない:
- record layout 変更
- visibility ロジック差し替え
- SSN プロトコル
- 既存テストへの影響 (全て緑のまま)

## 実装

### 新規ファイル
- `src/Quiver.Stores/EntityVersionMeta.cs` — 32B struct + Pstamp/Sstamp の sentinel 定数
- `src/Quiver.Stores/IEntityVersionStore.cs` — Read/Write/UpdateXmax/UpdatePstamp/UpdateSstamp
- `src/Quiver.Stores/NodeVersionStore.cs` — PagedFile ラッパ、NodeId.LocalId → (page, slot)
- `src/Quiver.Stores/RelationshipVersionStore.cs` — 同上
- `src/Quiver.Stores/PropertyVersionStore.cs` — 同上
- `tests/Quiver.Transactions.Tests/EntityVersionStoreTests.cs` — 4〜6 件の最小契約テスト

### 変更ファイル
- `src/Quiver.Storage/FileKindCatalog.cs` (もしくは Quiver.Wal の同等位置) — 3 FileKind 追加

### 完了条件
- `dotnet build Quiver.slnx` 0 warnings / 0 errors
- 既存 `dotnet test` 全緑 (件数据え置き + EntityVersionStoreTests 分追加)
- 新規 store は **どこからも呼ばれていない** (デッドコード相当、次タスクで配線)
- 既存 DB ファイルは format 変化なし (sidecar は新規 file kind なので old DB を開いても optional)

---

# FT-32: MVCC sidecar migration (record 縮小 + Xmin/Xmax を sidecar へ)

## 目的

Node/Relationship/Property record から Xmin/Xmax を撤去し、FT-31 で作った sidecar に移管する。
record layout が縮み、cache line residency が改善する。

## Scope

含む:
- NodeStore: 31B → 15B (Xmin/Xmax 削除)
- RelationshipStore: 64B → 48B (Xmin/Xmax 削除、Pad の扱い再検討)
- PropertyStore: 57B → 41B (Xmin/Xmax 削除)
- 全 callsite の Visibility access を sidecar 経由に書き換え
- `RawNodeRecord` などの diagnostics struct から Xmin/Xmax 削除
- `Visibility.IsVisibleAmbient` / `Visibility.IsVisible` の引数を `EntityVersionMeta` 化
- Bulk loader (NodeStore.BulkWrite 等) を sidecar 書き込みに対応
- Vacuum (OP-3) / Truncate (OP-5) を sidecar 対応
- Recovery Pass 0 (CommittedTxRegistry 再構築) を sidecar 経由に
- `FormatVersion` を V2Mvcc → V3MvccSidecar に bump
- 開発フェーズなので **互換読み出しコードは入れない** — 旧 DB は `FormatVersionMismatchException`
- FT-26 関連の `MvccVisibilityTests` / `MvccVisibilityProperties` を sidecar 経由 access に追従

含まない:
- SSN プロトコル (sidecar の Pstamp/Sstamp は 0 / long.MaxValue の初期値のまま使われない)
- `IsolationLevel.Serializable` の有効化

## 実装上の注意

- **breaking format change**: 既存 DB は読めなくなる。develop ブランチなので OK だが、
  PR 説明で明示
- WAL に乗る page 種別が 3 増えるので、`MvccThroughputRunner` の 1-tx あたり page 書き込み量が
  増える可能性。FT-29 (per-tx coalescing) で吸収される想定だが、retention 数値の再測定は必要

## 完了条件
- `dotnet build Quiver.slnx` 0 warnings / 0 errors
- `dotnet test` 全緑 (FT-26 visibility property 含む)
- Crash contract (`tests/Quiver.Backend.Tests/`) 全緑 — sidecar も WAL covered で recovery
- `MvccThroughputRunner` 再測定: retention 70% 以上を目標 (sidecar I/O 分の劣化を許容)

---

# FT-33: SSN protocol (Serializable isolation 有効化)

## 目的

FT-32 で sidecar に空いている Pstamp/Sstamp フィールドを活用し、論文 §4 Algorithm 1 の
SSN commit protocol を実装。`IsolationLevel.Serializable` を opt-in で有効化する。

## Scope

含む:
- `src/Quiver.Transactions/SsnContext.cs` — per-tx { Pstamp, Sstamp, Reads, Writes }
- `src/Quiver.Transactions/SerializabilityException.cs` — DeadlockException 並列
- `Transaction` に nullable `SsnContext` フィールド (Serializable 時のみ生成)
- TxNodeStore / TxRelationshipStore / TxPropertyStore に read/write protocol hook
  - read: t.Pstamp/Sstamp 更新、reads set 追加、early abort check
  - write: t.Pstamp 更新、writes set 追加、reads set からの remove (self r:w 抹消)
- `TransactionManager.Commit()` Serializable 分岐:
  - pre-commit で論文 Algorithm 1 step 2-4 を実行
  - π(T) ≤ η(T) で `SerializabilityException` throw
  - post-commit で v.Pstamp / v.Sstamp を sidecar に書き戻し
- ITransactionManager docs / sample 反映

含まない:
- 5 canonical テスト (FT-34)
- bench (FT-34)
- lock 撤去 (将来別タスク — SSN は deadlock-free だが今は併存)
- phantom protection (既存 index versioning 依存)

## 完了条件
- `dotnet build Quiver.slnx` 0 warnings / 0 errors
- 既存テスト全緑 (Serializable 未指定 path は従来 SI と同一挙動)
- 手動 smoke: Serializable で簡単な write skew を起こすと `SerializabilityException` で abort
- 上記 smoke を temp test 1 件で確認 (FT-34 の正式 suite とは別、簡易確認)

---

# FT-34: SSN tests + benchmarks (検証スイート)

## 目的

論文 §2-3 の 5 canonical シナリオ + safe-retry property を unit test 化、最小 overhead bench
を整備して PR で数値報告。

## Scope

### Unit tests (`tests/Quiver.Transactions.Tests/SsnScenarioTests.cs`)
1. **IsolationFailure_WW** — `T1 ←w:x→ T2 ←w:x→ T1`
2. **AtomicityFailure_WR** — `T1 ←w:x→ T2 ←r:w→ T1`
3. **WriteSkew_RR** — `T1 ←r:w→ T2 ←r:w→ T1`
4. **ReadOnlyAnomaly** — Fekete 2004 の 3-tx pattern
5. **DangerousStructure** — Cahill SSI の 3-tx pattern with T3 commits first
6. **SafeRetry** — SSN abort された tx を即時 retry → 必ず commit

順序制御は `CountdownEvent` / `ManualResetEventSlim` (既存 DeadlockDetectorTests と同パターン)。

### Benchmark (`benchmarks/Quiver.Benchmarks/SsnOverheadBenchmark.cs`)
- `ReadHeavy_SI` vs `ReadHeavy_SSN` — 1000 read/tx
- `WriteHeavy_SI` vs `WriteHeavy_SSN` — 100 write/tx

回帰 gate (TS-6) は今回追加せず、数値を PR 説明に記録。

### FT-26 retention 再測定
- `Ft26MvccThroughputRunner` を sidecar 化後の record で再実行
- retention 数値を skill task の FT-26 要約に追記

## 完了条件
- `dotnet build Quiver.slnx` 0 warnings / 0 errors
- `dotnet test tests/Quiver.Transactions.Tests/ --filter Category=Ssn` — 6 件全緑
- bench 実行可能 (`dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --filter '*SsnOverhead*'`)
- FT-26 retention 数値が記録される

---

## Skill task 整合性

- 既存 FT-30 は別タスク (HWM 安全化、完了済) なので、SSN 関連は **FT-31 〜 FT-34** で番号取得
- `.claude/skills/quiver-implement/tasks/feature.md` に 4 タスクのステップ節を追加
- `SKILL.md` の進捗テーブルに 4 行追加 (`未` 状態)

## 参照

- Wang, Johnson, Fekete, Pandis. "The Serial Safety Net: Efficient Concurrency Control on Modern Hardware". DaMoN'15. DOI 10.1145/2771937.2771949
- Cahill et al. "Serializable Isolation for Snapshot Databases". ACM TODS 34(4), 2009.
- Fekete et al. "A read-only transaction anomaly under snapshot isolation". SIGMOD Rec. 33(3), 2004.
- Quiver 内部: [src/Quiver.Core/EntityId.cs](src/Quiver.Core/EntityId.cs), [docs/design/07_transaction_recovery.md](docs/design/07_transaction_recovery.md), [.claude/skills/quiver-implement/tasks/feature.md](.claude/skills/quiver-implement/tasks/feature.md)
