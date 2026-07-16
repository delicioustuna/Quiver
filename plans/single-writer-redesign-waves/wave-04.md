# Wave 4 着手指示書: TransactionManager の Single Writer + snapshot readers

> 効力宣言: 本書と設計正本が食い違う場合は設計正本を優先し、食い違いをユーザへ報告する。
> 作成日: 2026-07-17
> 対応する正本のバージョン: 本書と同じ C-8 forward-fix commit に含まれる正本
> ステータス: 承認済み(2026-07-17、C-8 forward-fix)

## 1. 着手前チェック

- annotated tag `redesign-wave-3` が存在し、`develop` と `redesign/single-writer` は Wave 3 merge commit `963729b62895552cf4f8485b0d699ca8337234dd` に一致する。
- branch は `redesign/single-writer`、作業場所は専用 worktree `D:/csharp/Quiver-sw` であり、未コミット変更と `develop` 未マージ code commit はない。
- 正本 §2.1〜§2.3、§7.1〜§7.3、§9 Wave 4、§10.1〜§10.4、§11.2、§13 と review C-2、C-5、C-8、M-1、m-4、m-7 の境界が一致する。
- review C-8 は設計解決済みであり、実装完了は本書の metadata 縮約 gate で別に検証する。未対応 Critical を迂回する例外は設けない。
- Wave 3 の owner-bound property version、Generation sidecar、primary payload/store layout が実在し、pstamp/sstamp lane は SSN 専用の一時境界として残っている。
- 本書の目標状態と検証計画についてユーザの着手承認を得ている。

## 2. 読む順序

1. 正本 §9 Wave 4。
2. 正本 §2.1〜§2.3、§4.1〜§4.2。
3. 正本 §7.1〜§7.3、§8.1、§10.1〜§10.4。
4. 正本 §11.2、§13、§15〜§16。
5. review C-2、C-5、C-8、M-1、m-4、m-7。
6. `docs/spec/01_storage_paging.md`、`docs/spec/02_wal_recovery.md`、`docs/spec/03_mvcc.md`、`docs/design/00_conventions.md`、`docs/design/development.md`。

## 3. 目標状態と検証計画

Wave 4 は transaction manager、visibility、backend/facade adapter、transactional stores、bulk path、entity metadata を別タスクへ分解せず、one-writer snapshot architecture 全体を一つの target state へ変更する。

- **あるべき姿**: database instance ごとの `TransactionManager` が唯一の `WriterLease` と `SnapshotRegistry` を所有する。facade、backend、manager のどの入口から開始しても write transaction は同じ lease を取得し、既定は最大5秒待機、明示 fail-fast は即時拒否する。read transaction は開始時の `Snapshot(CommittedHighWater, AbortedGaps, ActiveWriterId?)` を登録し、writer lease、entity lock、WAL Begin を使わず、writer と並行して開始時点の一貫した version を読む。
- **visibility contract**: committed state は単一 active writer、committed high-water、high-water 以下の aborted gap に縮約する。reader は開始後の commit を観測せず、uncommitted `xmax` を無視する。writer 自身だけは `xmin/xmax = selfTxId` を自己可視性として扱い、create→read→update と savepoint rollback 後の読取りを保証する。
- **削除済みであるべき旧構造**: `LockManager`、`LockMode`、`DeadlockDetector`、`DeadlockException`、`SsnContext`、`SerializabilityException`、entity/index lock hook、wait-for graph、ReaderWriter locking option/metric、SSN test/benchmark/runner を production/test/benchmark path に残さない。public `IsolationLevel` と旧開始 API は Wave 6 の public cutover まで facade adapter として残すが、Serializable/ReadCommitted の実装分岐と新旧 internal interface の二重公開は残さない。
- **metadata contract**: SSN call site の削除と同じ変更境界で `EntityVersionMeta`、`IEntityVersionStore`、`EntityVersionStore`、in-memory implementation を `(xmin,xmax,generation)` の24 byte recordへ縮約する。pstamp/sstamp、commit-stamp high-water、更新 API、page arithmetic、旧 sidecar format readerを削除し、三 lane の page-boundary、clean reopen、stale generationを検証する。一時 sidecarや旧40 byte fallbackは作らない。
- **transaction path**: internal backend/transaction pathを `BeginRead` / `BeginWrite` に分け、既存 facade/custom backend SPI はその adapter とする。Tx Vertex/Edge/Nexus/Property/Index store は lock/SSN hookを持たず、snapshotと明示write contextを受ける。bulk load、autocommit mutation、schema/maintenance publish の既存入口もmanager leaseを迂回しない。
- **lifecycle contract**: commit、abort、commitなしdispose、savepoint、commit失敗、durable commit後publish失敗を一つのstate machineへ集約する。leaseとsnapshot registrationは全終了経路で一度だけ解放する。durable commit後の失敗はabortへ戻さずinstanceをfaultedにし、新operationを拒否する。同じtransaction handleの同期/asyncをまたぐ同時使用はtransaction-owned guardで拒否し、ambient/`ThreadStatic` contextは使わない。
- **診断 contract**: `SnapshotRegistry` はactive count、oldest age、開始位置を診断でき、閾値超過を警告する。安全性を壊すreader強制失効は行わない。reader handle/cursorのdispose漏れとwriter lease leakを反復testで検出する。
- **補修方法**: 最初にmanager、snapshot、internal read/write contract、Tx store、backend/facade adapter、metadataをtarget contractへ一括変更し、互換shimを作らない。続けてsolution buildのcompiler errorを不足call site一覧として補修する。build成功後にsingle-writer、snapshot、metadata、backend、bulk、transaction regression、全test、性能比較の順で契約漏れを補修する。

## 4. 本 Wave 固有の落とし穴

- facadeの既存`SemaphoreSlim`だけを残して正しさの根拠にしない。backend直呼び、manager直呼び、bulk/autocommit pathが同じmanager leaseを取得し、backendとmanagerの二重検査が迂回を防ぐ理由をsource commentに残す。
- multi-writer用`ActiveAtBegin`集合を名前だけ変えて温存しない。`CommittedHighWater`以下の欠番をaborted gapとして保持し、vacuumが参照versionを除去する前にpruneしない。
- readerをwriter leaseやentity lockで待たせない。page frameの短時間latchは物理メモリ安全性のため残してよいが、snapshot semanticsの根拠にしない。
- read-only transactionはWAL Begin/Abort、before-image、write setを生成しない。WAL bytes 0 testを単なるmock call countではなく同じbackend pathで検証する。
- public `IsolationLevel` と旧開始APIはWave 6まで残るため、型名のrepo全体0件を要求しない。productionのSerializable/ReadCommitted分岐、SSN/lock hook、option/metricは0件にする。
- `EntityVersionMeta`の三lane化はSSN削除と同時に行い、40byte layout、commit stamp header、`UpdatePstamp`/`UpdateSstamp`だけをdead codeとして残さない。旧sidecarを受理するfallbackは新formatのclean breakに反する。
- faulted commitでleaseを解放してもinstanceのfault flagを解除しない。逆にdurability境界前の失敗はin-process before-imageを適用し、二重解放しない。
- same-handle concurrent-use検出をthread id固定にしない。通常の`await`継続は許可し、同時に進行する別flowだけを`ConcurrentTransactionUseException`で拒否する。
- `SnapshotRegistry`のleak警告は観測機能であり、安全性を壊すtimeout強制disposeを導入しない。開始位置の収集コストをread hot pathで無制限に増やさない。
- Wave 5のwinner redo、sharp checkpoint統合、crash recoveryを前倒ししない。commit WAL orderingとrecovery winner判定を変更する必要が出た場合は実装を止め、process §5で設計をforward-fixする。
- Wave 6のpublic `IReadTransaction` / `IWriteTransaction`、Query/Mutate、Schema/EditSchema、custom backend SPI cutoverを前倒ししない。既存public facadeはinternal target pathのadapterに限定する。

## 5. 完了確認

| gate | 適用/N/A | コマンドまたは差分根拠 | 合格条件 |
|---|---|---|---|
| 機能 test | 適用 | `dotnet build Quiver.slnx -v minimal`、`Quiver.Transactions.Tests`、`Quiver.Backend.Tests`、`Quiver.Tests`のsingle-writer/snapshot/metadata/bulk focused test、solution全test project | 0 errors、0 warnings、全対象test成功。facade/backend/managerの全入口が同じwriter leaseを守り、readerがwriterを待たず開始時snapshotを読む |
| crash test | N/A | `redesign-wave-3`との差分でCommit/PageImage/fsync ordering、winner判定、checkpoint/recoveryを変更しないことを確認する。read transactionのWAL 0 bytesは機能testで検証する | Wave 5対象のdurability/recovery変更がなく、commit ordering差分が0件である。該当差分が必要になった場合はN/Aを撤回してprocess §5へ戻る |
| baseline gate | 適用 | redesign baselineと同一環境の32 readers + 1 writer runner、readerなしwriter比較、`--basic-perf`のcomparable CRUD/visibility/traversal、durable point update | readerがwriterを待たず、writer commit p50がreaderなし比1.5x以内。comparable各p50がbaseline比1.20x以内。durable point update p50が3491.40µs以内 |
| as-built 更新 | 適用 | `docs/spec/01_storage_paging.md`、`02_wal_recovery.md`、`03_mvcc.md`、`docs/design/development.md`の実装mapとactive docs scan | one-writer snapshot、read WAL 0、metadata三lane、internal adapter、診断/lifecycleの実装済みcontractと一致する |

追加条件は次のとおりである。

- facade、backend、managerの三境界から二本目のwriterを開始するtestがwait、fail-fast、5秒既定/明示timeoutを検証する。
- bulk load、vector autocommit、schema/maintenance publishの既存入口がmanager leaseを迂回しない。
- 32 readersはactive writer中も開始・進行し、writer commit後も開始時snapshotからdriftしない。writerはreader終了を待たずcommitする。
- long readerは開始後のcreate/update/deleteを観測せず、新readerはcommit済みversionを観測する。
- writerは自身のcreate/update/deleteを正しく観測し、savepoint rollback後はundo済みversionを観測しない。
- aborted gapを後続commit済みtxと誤認せず、gap pruneは参照record回収前に行われない。
- read transactionはWAL bytes、before-image、write setを増やさない。
- commit、abort、commitなしdispose、commit失敗、faulted遷移の各経路でwriter leaseを一度だけ解放し、faulted instanceは新operationを拒否する。
- 同じtransaction handleの同時使用は同期/async flowを問わず専用例外で拒否し、通常のawait継続は成功する。
- `SnapshotRegistry`のactive count、oldest age、開始位置、閾値超過警告が検証され、readerを強制失効しない。reader handle/cursor disposeの反復後にregistrationが残らない。
- production sourceから`LockManager`、`LockMode`、`DeadlockDetector`、`DeadlockException`、`SsnContext`、`SerializabilityException`、entity/index lock hook、wait-for graph、pstamp/sstamp、`UpdatePstamp`、`UpdateSstamp`、commit-stamp high-waterの参照が0件である。
- public `IsolationLevel`と旧開始APIはadapterとしてのみ残り、production sourceにSerializable/ReadCommittedの実装分岐が0件である。
- `EntityVersionMeta`は`(xmin,xmax,generation)`だけを持つ24byte recordで、page arithmetic、page-boundary、clean reopen、format mismatch、same-sequence/different-generation rejection testが成功する。
- SSN/locking test、benchmark、runnerは削除され、replacementの`SingleWriterContractTests`と`SnapshotReaderTests`がfacade/backend/manager、reader/WAL/diagnostic contractを保証する。
- staged pathに対する`scripts/agent-guardrails/check-track-markers.ps1`と`git diff --check`が成功する。
- branch tipは完成または検証済み補修commitであり、topic branchへpush済みである。

mergeとtagはユーザの明示承認を別々に得る。
