# Wave 5 着手指示書: commit/checkpoint/recovery の統合

> 効力宣言: 本書と設計正本が食い違う場合は設計正本を優先し、食い違いをユーザへ報告する。
> 作成日: 2026-07-17
> 対応する正本のバージョン: 本書と同一のdoc-only commit（基点: `9585ba99f07414ccac5e02c04c947bfa02914831`）
> ステータス: 承認済み(2026-07-17)

## 1. 着手前チェック

- annotated tag `redesign-wave-4` が存在し、`develop` と `redesign/single-writer` は Wave 4 merge commit `0e7c3bb04396a36b70c052bf85b2bee5fd4b2103` に一致する。
- branch は `redesign/single-writer`、作業場所は専用 worktree `D:/csharp/Quiver-sw` であり、未コミットのコード変更と `develop` 未マージ commit はない。
- 正本 §2.5、§4.2〜§4.3、§6.2、§7.3〜§7.4、§9 Wave 5、§10.3〜§10.4、§11.2、§13 と review C-1、M-1、M-2 の境界が一致する。
- review C-1 は no-steal / no-force と redo-only recovery で対応済み、M-2 は writer lease 下の sharp checkpoint で対応済みである。M-1 により primary store の crash reopen は本 Wave の検証対象である。
- review C-5〜C-7 の logical identity materialization、relationship reuse、query candidate 境界は本 Wave で再定義しない。recovery から該当 contract の変更が必要になった場合は実装を止め、process §5 の設計 forward-fix に戻る。
- Wave 2 の新 WAL family、7 record、strict Commit winner scanner、checkpoint pair scannerと、Wave 4 の database 単位 `WriterLease`、transaction-owned `WalWriteSet`、faulted lifecycle が実在する。
- 現行 `PagedFile` の dirty eviction、factory の presumed-committed recovery horizon、checkpoint の lease 境界は本 Wave で target contract へ統合する対象であり、互換経路として残さない。
- 本書の目標状態と検証計画についてユーザの着手承認を得てから、承認済み doc-only commit を push する。

## 2. 読む順序

1. 正本 §9 Wave 5。
2. 正本 §2.5、§4.2〜§4.3、§6.1〜§6.2。
3. 正本 §7.3〜§7.4、§10.3〜§10.4。
4. 正本 §11.2、§13〜§16。
5. review C-1、M-1、M-2。
6. `docs/spec/01_storage_paging.md`、`docs/spec/02_wal_recovery.md`、`docs/spec/03_mvcc.md`、`docs/design/development.md`。

## 3. 目標状態と検証計画

Wave 5 は commit、buffer eviction/flush、checkpoint、recovery、binary backend open を別タスクへ分解せず、単一の durability contract へ変更する。

- **あるべき姿**: primary entity/property version/vector payload/schema catalog と、それらを支える page metadata は同じ transaction-owned write set に参加する。同一 page の最終 after-image を `PageImage` として `Commit` より前に書き、checksum が有効な `Commit` の LSN まで fsync できた場合だけ durable winner とする。`PageImage`、page LSN、close-time flush から commit を推定しない。
- **no-steal / no-force contract**: committed structure から到達可能な未 commit dirty page は、commit fsync 前に data fileへ書かず、eviction、remap、close、checkpoint、buffer pressure のいずれもこの規則を迂回しない。committed allocation high-water を超える新規 page は到達不能な append として commit 前の物理確保を許すが、root/catalog publish と対応 `PageImage` は strict Commit 境界に従う。commit は data page flush を待たない。
- **oversized transaction contract**: transaction-owned dirty set が設定された安全容量を超え、no-steal のまま frame を確保できない場合は `TransactionTooLargeException` で durability 境界前に abort する。before-image をプロセス内で逆順適用し、writer lease と snapshot registration を一度だけ解放する。未 commit page を eviction する fallback は作らない。bulk path は既存の chunk commit 境界を使う。
- **sharp checkpoint contract**: checkpoint は `TransactionManager` の同じ writer lease を取得し、active writer の終了を待つ。reader の終了は待たない。`CheckpointBegin` fsync、committed dirty page flush、catalog/index flush、`CheckpointEnd` fsync、古い WAL truncate の順を固定し、各 phase の crash から前回完了 checkpoint または新しい完了 checkpointへ収束する。threshold/adaptive/manual/close の全入口で lease と phase ordering を迂回しない。
- **redo-only recovery contract**: binary backend は通常 operation を受け付ける前に database/WAL header、record/page checksum、最後の完了 checkpoint を検証し、有効な `Commit` を持つ winner の `PageImage` と durable `FileTruncate` だけを page LSN 順に冪等適用する。loser は redo 対象から除外し、WAL undo pass、presume-committed、FT 専用 recovery passを持たない。
- **reopen contract**: recovery は committed registry、next transaction ID、allocation/Generation high-water を checkpoint 済み primary catalog と winner redo から復元する。健全な catalog では全 slot scanを行わず、不健全なら通常 open を続行せず明示 repair を要求する。必須 primary payload が欠損した場合は corruption とし、derived access path の不健全性は rebuild/fallback contractへ分離する。
- **atomicity contract**: property version と `VectorPayloadRef`、payload page、schema/catalog root は全 commit/kill 境界で一緒に残るか一緒に消える。horizon 上で到達可能な property version が欠損 payload を指す状態を許さない。abort/savepoint はメモリ内 before-image だけを使い、recovery undo record は追加しない。
- **削除済みであるべき旧構造**: presume-committed の分岐・命名・root horizon、recovery undo pass、FT logical recovery pass、未 commit dirty eviction、旧 checkpoint writer/record解釈、`PageImage` 存在による winner 推定を production path に残さない。
- **補修方法**: 最初に write set 所有権、dirty frame state、commit ordering、checkpoint lease/phase、recovery/open を target contract へ一括変更する。続けて solution build の compiler error を不足 call site 一覧として補修する。build 成功後は WAL/transaction/storage の focused test、backend process-kill matrix、checkpoint matrix、100反復 recovery、全 test、性能比較の順で契約漏れを補修する。
- **契約保証**: `WalWinnerLoserTests`、no-steal eviction/oversize test、checkpoint 5 phase test、payload/ref atomicity test、binary backend open/recovery testを追加・更新する。as-built は parser foundation という記述を統合 durability contractへ更新し、source commentには no-steal の理由、到達不能な新規 pageだけを例外にできる理由、strict Commit ordering、durable commit後にabortへ戻せない理由を残す。

## 4. 本 Wave 固有の落とし穴

- `PagedFile.EvictFrame` で WAL を flushして dirty pageを書けば WAL-before-dataにはなっても no-stealにはならない。owner transaction の durable Commit 前かどうかを frame/write-set stateで判定し、未 commit dirty frameをdata fileへ書かない。
- `AllocatePage`、free-list/meta page、MMF remap、file growthが直接 fsyncする現行経路を一律禁止しない。正本が許す到達不能な新規 page appendと、commit前に公開してはならないroot/allocation metadataを分ける。
- `WriteAheadLog.ActiveWriteSet` をdatabase全体のambient slotへ戻さない。Wave 4 の単一 writer ownershipを利用しても、read transaction、checkpoint、recoveryからtransaction-owned stateを誤参照しない境界を維持する。
- checkpointをcommit後の同期コールだけに閉じず、manual、threshold、adaptive、close入口を同じwriter leaseと5 phaseへ集約する。checkpoint待機中も既存readerは進行でき、新readerもsnapshot登録できる。
- `CheckpointEnd` 後のtruncateで、recoveryに必要なcheckpoint pairやlive tailを消さない。truncate途中のcrashと`FileTruncate` replayを別の意味として混同しない。
- torn WAL tail、未知record、checksum不一致を「末尾だから安全」と黙って受理しない。正本のcorruption contractどおりfail-fastし、process-kill testが意図するdurability cutだけをfault injectorで作る。
- `RecoveryHorizon` や「これ未満はcommitted」という presumed-committed shortcutを名前だけ変えて残さない。winner、checkpoint済み committed catalog、aborted gaps の責務を明示する。
- `WritePageForRecovery` は page LSN を比較して新しい/同一 image の再適用を冪等にし、古い WAL imageで新しい pageを上書きしない。checksum検証をrecovery writeで迂回しない。
- payload atomicity testをmockのappend順だけで済ませない。同じbinary backendをprocess killしてreopenし、primary property readとconsistency checkで判定する。
- Wave 6 のpublic transaction/query/schema cutover、Wave 7/8のderived segment recovery、Wave 9のvacuum/reuse coordinatorを前倒ししない。該当contract変更が必要ならprocess §5へ戻る。

## 5. 完了確認

| gate | 適用/N/A | コマンドまたは差分根拠 | 合格条件 |
|---|---|---|---|
| 機能 test | 適用 | `dotnet build Quiver.slnx -v minimal`、`dotnet test tests/Quiver.Wal.Tests/Quiver.Wal.Tests.csproj --no-build`、`dotnet test tests/Quiver.Transactions.Tests/Quiver.Transactions.Tests.csproj --no-build`、`dotnet test tests/Quiver.Storage.Tests/Quiver.Storage.Tests.csproj --no-build`、`dotnet test tests/Quiver.Backend.Tests/Quiver.Backend.Tests.csproj --no-build --filter "Category!=Chaos"`、`dotnet test tests/Quiver.PropertyTests/Quiver.PropertyTests.csproj --no-build`、solution全test project | 0 errors、0 warnings、全対象test成功。strict Commit、no-steal、redo-only、sharp checkpoint、payload/ref atomicityが同じbinary pathで成立する |
| crash test | 適用 | `BinaryGraphStorageBackendCrashContractTests`、`Category=Chaos`、checkpoint 5 phase、commit kill matrix、torn WAL/page、truncate、payload/ref crash、100反復recoveryをbinary backendで実行 | commit返却済みwinnerは必ず残り、Commit無しloserは必ず消える。全kill pointが期待するreopen成功または明示corruptionへ収束し、欠損payload参照がない |
| baseline gate | 適用 | redesign baselineと同一環境で`--basic-perf`のdurable point updateを測定し、`redesign-wave-4`とWave 5の双方で`--clean-slate-page-wal-baseline 20 200 5000 20 1000 20`のRAG ingest amplification/payload/index内訳を測定する | durable point update p50が3491.40 us以内、RAG ingest WAL amplificationが同一環境のWave 4比1.00x以内。2026-07-17の参照値はWave 4が16.37x、Wave 5が16.26x。測定環境、commit、生出力を`docs/benchmarks/`へ保存する。11.74xはWave 8のfull-text segment WAL gateとする |
| as-built 更新 | 適用 | `docs/spec/01_storage_paging.md`、`docs/spec/02_wal_recovery.md`、`docs/spec/03_mvcc.md`、`docs/design/development.md`の実装mapとactive docs scan | parser foundation/後続Wave注記、presumed-committed、未統合checkpoint記述が残らず、no-steal/no-force、strict Commit、sharp checkpoint、redo-only open/recoveryと一致する |

追加条件は次のとおりである。

- primary entity/property/payload/schema catalogの変更が同じ`WalWriteSet`に参加し、同一pageの`PageImage`がlatest-winsで一件にcoalesceされる。
- Commit append前、Commit record途中、Commit fsync後、in-memory publish途中の各kill pointで、明示Commitのdurability境界とfaulted lifecycleが一致する。
- committed structureから到達可能な未commit dirty pageはeviction、remap、close、checkpoint、buffer pressureでdata fileへ書かれない。
- 到達不能な新規page appendはCommit無しreopenでfree/unreachableのままであり、Commit済みならcatalog/rootから到達できる。
- dirty set安全容量超過は`TransactionTooLargeException`でabortし、before-image適用後にwriter lease、snapshot registration、active write setが残らない。
- abortとsavepoint rollbackはWAL undo recordを生成せず、process内before-imageでprimary page、payload ref、catalog mutationを復元する。
- checkpointはactive writerの終了を待ち、readerは待たない。threshold、adaptive、manual、closeの全入口が同じleaseとBegin→data/catalog/index flush→End→truncate順序を守る。
- checkpoint 5 phaseの各process killで未完了pairを採用せず、完了pairだけをredo起点にし、truncate後reopenでもwinnerを失わない。
- recoveryは有効なCommitを持つwinnerだけをredoし、Commit無し/Abort済みloserを除外する。undo passとPageImageからのwinner推定はない。
- page LSN比較によりwinner redoと`FileTruncate`再適用は冪等であり、同じdatabaseに100回recoveryしてもprimary exportとchecksumが変化しない。
- torn WAL tail、unknown record、record/page checksum不一致、対応しないcheckpoint Endは黙って読み飛ばさず、定義済み例外でfail-fastする。
- committed registry、next transaction ID、allocation/Generation high-waterは健全なcheckpoint済みcatalogから復元され、通常openで全slot scanしない。
- property version、`VectorPayloadRef`、payload pageは全commit/kill境界でatomicであり、horizon上到達可能なpropertyが欠損payloadを指さない。
- production sourceのpresume-committed分岐・命名、recovery undo/FT pass、legacy checkpoint writer/decoder、未commit dirty evictionが0件である。
- staged pathに対する`scripts/agent-guardrails/check-track-markers.ps1`と`git diff --check`が成功する。
- branch tipは完成または検証済み補修commitであり、topic branchへpush済みである。

mergeとtagはユーザの明示承認を別々に得る。
