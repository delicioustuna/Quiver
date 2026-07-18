# Wave 6 着手指示書: unified scalar index と query/traversal

> 効力宣言: 本書と設計正本が食い違う場合は設計正本を優先し、食い違いをユーザへ報告する。
> 作成日: 2026-07-18
> 対応する正本のバージョン: `ede755edac004606e618bf8d77e3d8f01d7f2eb6`
> ステータス: 実装・検証完了(2026-07-18)

## 1. 着手前チェック

- annotated tag `redesign-wave-5` が存在し、`develop` と `redesign/single-writer` は Wave 5 merge commit `93b87022fc36841dc00fa8a988cb83ada0b41e94` に一致する。
- branch は `redesign/single-writer`、作業場所は専用 worktree `D:/csharp/Quiver-sw` であり、未コミット変更と `develop` 未マージ commit はない。
- 正本 §2.1〜§2.4、§4.1、§4.4、§5.1、§5.3〜§5.4、§7.1〜§7.3、§7.5〜§7.6、§8.1、§8.4、§9 Wave 6、§10.3〜§10.4、§11.2、§13 と review C-2、C-5、C-7、M-5 の境界が一致する。
- review C-2 は SSN と lock 分岐を Wave 4 で削除し、public `IsolationLevel` と旧 transaction API の削除を本 Wave の cutover に集約する設計で解決済みである。
- review C-5 と C-7 は本 Wave の query/traversal/index output で実装検証する。logical pipeline は full typed ID を保持し、physical lookup へ渡す Sequence は primary `Read` 検証直後の値に限定し、derived candidate は current Generation と snapshot で再検証する。
- review C-6 の relationship Sequence 再利用解放 coordinator は Wave 9 の対象であり、本 Wave は no-reuse contract を維持する。raw entry の lifetime や free release 順序の変更が必要になった場合は実装を止め、process §5 の設計 forward-fix に戻る。
- Wave 3 の owner-bound property version、Wave 4 の internal read/write path・snapshot visibility・identity materializer、Wave 5 の transaction-owned page WAL と reopen/recovery が実在する。
- 現行の旧 public `IGraphTransaction`、`IsolationLevel`、`ISchemaApi`、`GraphTraversalSource` 内 mutation、raw-long scalar index value、非 transactional schema mutation は本 Wave で target contract へ置換する入力であり、compatibility surface として残さない。
- 本書の目標状態と検証計画についてユーザの着手承認を得てから、`ステータス: 承認済み(2026-07-18)` に更新し、doc-only commit を push する。

## 2. 読む順序

1. 正本 §9 Wave 6。
2. 正本 §2.1〜§2.4、§4.1、§4.4、§5.1、§5.3〜§5.4。
3. 正本 §7.1〜§7.3、§7.5〜§7.6、§8.1、§8.4。
4. 正本 §10.3〜§10.4、§11.2、§13〜§16。
5. review C-2、C-5、C-7、M-5。
6. `docs/spec/00_overview.md`、`docs/spec/03_mvcc.md`、`docs/spec/04_records_index.md`、`docs/spec/05_query.md`、`docs/spec/08_known_limits.md`、`docs/design/development.md`。

## 3. 目標状態と検証計画

Wave 6 は public transaction/schema/query cutover、scalar index の永続 contract、planner/operator/DSL、typed/SourceGen/bulk/migration の mutation 経路を別タスクへ分解せず、単一の利用者 contract へ変更する。

- **public transaction contract**: `QuiverDatabase` は `BeginReadTransaction()` と `BeginWriteTransaction()` を公開する。`IReadTransaction` は snapshot identity、entity/property read、scan、`Query`、read-only `Schema`、cursor lifetime と dispose を持つ。`IWriteTransaction : IReadTransaction` は mutation、savepoint、commit/abort、`Mutate`、`EditSchema` を追加する。concrete handle は `ReadTransaction` と `WriteTransaction` に分け、read handle から runtime cast で write capability を得られないようにする。
- **atomic public cutover**: 全 public signature、Client DSL、typed CRUD、SourceGen、migration、bulk load、custom backend SPI を新 read/write transaction contract へ一括変更する。同じ変更境界で旧 `IGraphTransaction`、旧 `GraphTransaction` public model、`IsolationLevel`、`BeginTransaction(IsolationLevel)`、`BeginReadOnlyTransaction()`、旧 backend transaction factory を削除し、新旧 model の alias、obsolete shim、extension adapter を残さない。
- **query/mutation capability contract**: `IReadTransaction.Query` は mutation member を持たない read-only `GraphTraversalSource` を返す。`IWriteTransaction.Mutate` は `GraphMutationSource` を返し、create、merge、upsert と builder をこの型だけに置く。同じ write transaction 内の read subquery は `Query` を明示して使い、read transaction や read DSL から mutation API へ到達できない。
- **schema contract**: `IReadTransaction.Schema` は list/get/try-get だけを持つ `ISchemaCatalog`、`IWriteTransaction.EditSchema` は `ISchemaEditor : ISchemaCatalog` を返す。token、index definition、create/drop/alter は write transaction の commit/abort と同じ durable boundary に参加する。read-only query の未知 label/type/role/property token は catalog を変更せず空候補へ解決する。
- **unified index definition contract**: public `PropertyTarget`、abstract `IndexDefinition`、`ScalarIndexDefinition` と、永続 `IndexDefinitionCatalog` を導入する。scalar schema API は `CreateIndex(IndexDefinition)` に統一し、target は owner kind、property key、任意の label/edge type/nexus type scope を表す。definition と lifecycle state `Ready | RebuildRequired | Building` は primary metadata として WAL、checkpoint、reopen の対象にする。
- **scalar access path contract**: B+Tree の value は raw owner ID ではなく owner identity と `PropertyVersionRef` を結び付ける derived reference にする。seek/range/label/type path は candidate の owner Generation、entity visibility、property version visibility、property target を primary store で再検証し、stale、orphan、別 generation、別 snapshot の candidate を skip する。index entry を primary data や可視性の正本にしない理由を source comment に残す。
- **logical/physical identity boundary**: logical IR、planner、operators、Client traversal、Match、typed traversal は full `VertexId` / `EdgeId` / `NexusId` を保持する。physical locator、adjacency chain、index keyへ渡す Sequence は full typed ID の primary `Read` 成功直後だけに取り出す。`LabelVertexIndex.Lookup` と scalar index output は full typed ID を返し、diagnostic raw long や Wave 7 まで残る vector compatibility bridgeを query/traversal input に流入させない。
- **definition-driven maintenance**: typed CRUD、fluent mutation、SourceGen、bulk load、migration の property set/update/remove は同じ definition matching と index commit batch を通る。reopen 後は利用者が `CreateIndex` を再実行しなくても、永続 definition と property target に一致する mutation が maintenance 対象になる。
- **rebuild/fallback contract**: derived scalar index の欠落、checksum不整合、orphan、manifest不整合は primary corruption と区別し、definition を `RebuildRequired` にする。query は同じ snapshot の primary property scanへ fallbackし、結果集合と順序規則を index path と一致させる。read snapshot から rebuild artifact を作り、writer lease 下の短い publishで source generation を再検証して `Ready` へ切り替える。
- **graph mutation/read contract**: `GraphMutationSource` は edge を `(source, type, target)`、nexus を `(type, role付きmember集合)` で冪等 merge し、利用者側の全隣接・全 incidence 線形走査を不要にする。read transaction は vertex label、edge type、nexus typeを取得でき、typed/fluent/SourceGenの結果を同じ primary read pathで返す。
- **planner/operator contract**: snapshot と `PropertyTarget` を logical IR から全 physical operatorへ明示的に渡す。scalar seek/range、label/type seed、expand、predicate、Match、merge lookupは同じ snapshotで評価し、index pathとbase scanの traversal resultを一致させる。`EntityKind.Property` と旧 vector-special query branchは削除する。
- **削除済みであるべき旧構造**: production/public surfaceから旧 transaction API、`IsolationLevel`、read DSL内 mutation、transaction外 schema mutation、label固定 index target、raw-long logical scalar candidate、`EntityKind.Property` branch、旧 vector-special query branchを除去する。Column/ScalarColumn/DirectArray join の再導入と Wave 7/8 の vector/full-text segment化は行わない。
- **補修方法**: 最初に public transaction/backend SPI、schema/catalog、index value/lifecycle、logical IR/planner/operators、Client/SourceGen/bulk/migrationを target contractへ一括変更する。続けて solution build の compiler errorを旧 call site一覧として補修する。build成功後は PublicApi、backend transaction contract、schema/index、operator、Client、SourceGen、bulk/migration、reopen/rebuild、stale candidate、全 test、性能比較の順で契約漏れを補修する。
- **契約保証**: public read/write capability分離、custom backend begin contract、未知 token非 mutation、schema commit/abort、definition reopenと自動 maintenance、全 mutation経路同値性、seek/range old/new snapshot、same-sequence/different-generation、vertex/edge/nexus target、index delete/corrupt fallback/rebuild、merge冪等性、label/type read、traversal equivalenceを回帰 testへ追加・更新する。as-builtは新 public API、transactional schema、unified scalar index、candidate revalidation、fallback/rebuildを実装済み contractとして更新する。

## 4. 本 Wave 固有の落とし穴

- public interface 名だけを先に追加して旧 `IGraphTransaction` へ委譲する shim を作らない。SourceGen golden、typed model、Client DSL、samples、custom backend contractまで同じ最終 signatureへ切り替える。
- `IWriteTransaction : IReadTransaction` でも、read-only `Query` から mutation builderを返さない。mutation入口は `Mutate`、schema mutation入口は `EditSchema`に分離する。
- `ISchemaCatalog.TryGet*` と planner の未知 token 解決で `GetOrCreate*` を呼ばない。read transactionとread queryの token store、WAL、database bytesが不変であることを testする。
- schema/index definitionを process-global dictionaryだけに置かない。create/drop/alterのabort、commit、reopen、checkpoint/recovery後に primary catalogとin-memory viewが同じ状態へ収束するようにする。
- B+Tree valueを owner packed IDだけに置き換えて終えない。`PropertyVersionRef`を保持し、同じ ownerのproperty update/deleteとold readerで適切なversionを再検証できるようにする。
- stale candidate検証を operatorごとに任意実装しない。scalar seek/range、label/type、merge lookupが共有するmaterialization/primary validation境界を設け、Generation `0`やraw Sequenceから logical IDを再構成しない。
- index破損時のfallbackで新しい snapshotを取り直さない。元のread transactionのsnapshotを保持し、index pathとbase scanが同じ結果を返すようにする。
- rebuild中のdefinitionを無条件に`Ready`へpublishしない。source definition/data generationを再検証し、変化していればartifactを破棄して再試行する。重いbuild中にwriter leaseを保持しない。
- edge/nexus mergeを利用者側の全件・全隣接線形走査で実装しない。dedicated scalar/property targetまたは既存physical lookupを使い、candidateをprimary revalidationしたうえで冪等性を判定する。
- bulk loader、migration、SourceGenだけがindex maintenanceを迂回するbootstrap pathを残さない。property mutationの意味は入口に依存せず、同じcommit batchへ集約する。
- current as-builtにはWave 4完了後もSSN stampと記すdriftがある。本Waveで変更するpublic/query/index contractと、既に実装済みのthree-lane metadataを混同せず、active docsを実装状態へ揃える。
- Wave 7の`EntityCandidateSet`/vector raw-long bridge削除、Wave 8のfull-text segment、Wave 9のrelationship reuse coordinatorとmigration history storeを前倒ししない。該当contract変更が必要ならprocess §5へ戻る。

## 5. 完了確認

| gate | 適用/N/A | コマンドまたは差分根拠 | 合格条件 |
|---|---|---|---|
| 機能 test | 適用 | `dotnet build Quiver.slnx -v minimal`、`dotnet test tests/Quiver.PublicApi.Tests/Quiver.PublicApi.Tests.csproj --no-build`、`dotnet test tests/Quiver.Backend.Tests/Quiver.Backend.Tests.csproj --no-build --filter "Category!=Chaos"`、`dotnet test tests/Quiver.Index.Tests/Quiver.Index.Tests.csproj --no-build`、`dotnet test tests/Quiver.Operators.Tests/Quiver.Operators.Tests.csproj --no-build`、`dotnet test tests/Quiver.Client.Tests/Quiver.Client.Tests.csproj --no-build`、`dotnet test tests/Quiver.SourceGen.Tests/Quiver.SourceGen.Tests.csproj --no-build`、`dotnet test tests/Quiver.Tests/Quiver.Tests.csproj --no-build`、`dotnet test tests/Quiver.PropertyTests/Quiver.PropertyTests.csproj --no-build`、solution全test project | 0 errors、0 warnings、全対象test成功。public capability分離、transactional schema、definition-driven scalar maintenance、candidate revalidation、fallback/rebuild、traversal equivalenceがbinary/in-memory両backendで成立する |
| crash test | 適用 | binary backendでschema/index definitionのcommit前後、index page/manifest publish、rebuild publishのkill matrixを実行し、`Category=Chaos`のindex manifest対象とdefinition reopen/fallback testを実行する | Commit無しdefinition/mutationは消え、Commit済みdefinitionとprimary propertyは残る。derived indexの欠落/破損はprimary openを壊さず`RebuildRequired`とsame-snapshot base-scan fallbackへ収束し、rebuild後にindex pathへ戻る |
| baseline gate | 適用 | redesign baselineと同じ環境で`dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --basic-perf`、`dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --clean-slate-page-wal-baseline`、`dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --clean-slate-csr-product-integration`、`dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --nexus-traversal`を実行し、必要に応じて`--filter "*ApplyDyadic*"`でcandidate validation回帰を補助測定する | comparableなCRUD/visibility/traversal p50がbaseline比1.20x以内、述語付き2-hop p50が1.8982 ms以下、nexus traversalのdegree 10/100/1000が各binary/view比3.0x以内。環境、commit、生出力を`docs/benchmarks/`へ保存する |
| as-built 更新 | 適用 | `docs/spec/00_overview.md`、`docs/spec/03_mvcc.md`、`docs/spec/04_records_index.md`、`docs/spec/05_query.md`、`docs/spec/08_known_limits.md`、`docs/design/development.md`、README、samplesのactive contract scan | 旧transaction/schema/query例、raw scalar candidate、非transactional index definition、旧SSN metadata記述が残らず、新public API、unified scalar index、candidate revalidation、fallback/rebuildと一致する |

追加条件は次のとおりである。

- `IReadTransaction`、`IWriteTransaction`、`ReadTransaction`、`WriteTransaction`、`ISchemaCatalog`、`ISchemaEditor`、`GraphMutationSource`のpublic surfaceに日本語XML docがあり、PublicApi approvalが最終contractだけを含む。
- production source、tests、samples、SourceGen出力、active docsに旧`IGraphTransaction`、public/internal `IsolationLevel`、`BeginTransaction(IsolationLevel)`、`BeginReadOnlyTransaction()`の意図しない参照が0件である。
- facade、public custom backend SPI、internal backend/managerのread/write factoryが一致し、read handleからmutationまたはschema editorを取得できない。
- read-only queryで未知label、edge type、nexus type、role、property keyを使用してもtoken catalog、index catalog、WAL bytes、database bytesが変化せず、空結果になる。
- schema token/index definitionのcreate/drop/alterはcommitで可視化され、abort/savepoint rollbackでは残らず、reopen後も同じ`PropertyTarget`とstateを復元する。
- reopen後に`CreateIndex`を再実行せず、typed CRUD、fluent mutation、SourceGen、bulk load、migrationのset/update/removeが同じdefinition matchingとindex commit batchを通る。
- scalar B+Tree valueはowner identityと`PropertyVersionRef`を表し、seek/rangeの全candidateがowner Generation、entity visibility、property visibility、target scopeで再検証される。
- vertex、edge、nexusのsame-sequence/different-generation candidate、deleted owner、old property version、別scope candidateがlogical outputへ出ない。
- label/type lookupとlogical scalar seek/rangeはfull typed IDを返し、raw Sequenceまたはdiagnostic raw longがquery/traversal/transaction入力へ流入しない。
- index file/page/manifestの削除または破損時も同じsnapshotのbase scanが同じ結果を返し、rebuild publish後にindex pathへ戻る。
- update/deleteと並行するold readerが旧snapshotのindex/base結果を保持し、新readerは新versionだけを見る。
- edge mergeは`(source,type,target)`、nexus mergeは`(type,role付きmember集合)`で冪等であり、重複createを行わず、利用者側の線形全件走査を必要としない。
- read transactionからvertex label、edge type、nexus typeを取得でき、typed/fluent/SourceGen経路で同じ値を返す。
- index pathとbase scanでscalar predicate、range、Match、expandを含むtraversal結果が一致する。
- production query/operator sourceの`EntityKind.Property`分岐、旧vector-special scalar/query分岐、label固定index target、transaction外schema mutationが0件である。
- staged pathに対する`scripts/agent-guardrails/check-track-markers.ps1`と`git diff --check`が成功する。
- branch tipは完成または検証済み補修commitであり、topic branchへpush済みである。

mergeとtagはユーザの明示承認を別々に得る。

## 6. 完了記録

- public transaction、schema、query、mutation capability を新契約へ原子的に切り替え、旧 surface を削除した。
- scalar index definition、`PropertyVersionRef` value、candidate revalidation、vertex/edge/nexus target、自動 maintenance を実装した。
- rebuild は snapshot reader で artifact を構築し、source generation と reader horizon を再検証する background publish へ変更した。
- index path と same-snapshot primary fallback、schema rollback、未知 token 非 mutation、merge 冪等性、old/new reader を回帰テストで検証した。
- scalar definition の未 commit kill と、artifact 構築後、publish commit 前、publish commit 後の crash 境界を検証した。
- solution build は 0 warnings、0 errors で成功した。
- solution test は全 test project で成功した。
- BasicPerf の最大 baseline 比は 1.134x、述語付き 2-hop p50 は 1.2680 ms、Nexus view/binary の最大比は 1.09x で全 gate に合格した。
- 生出力は `docs/benchmarks/2026-07-18_SingleWriterRedesign_TransactionScalarIndex_*Raw.md` に保存した。
