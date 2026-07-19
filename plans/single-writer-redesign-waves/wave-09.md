# Wave 9 着手指示書: maintenance / logical / migrations / addons

> 効力宣言: 本書と設計正本が食い違う場合は設計正本を優先し、食い違いをユーザへ報告する。
> 作成日: 2026-07-19
> 対応する正本のバージョン: `689af54e9d1c8c676ff3ea36f4101b929bc2bd7e`
> ステータス: 承認済み(2026-07-19)

## 1. 着手前チェック

- annotated tag `redesign-wave-8` が存在し、`develop` と `redesign/single-writer` は Wave 8 merge commit `60fb42d725f7bc7a3679cc3630bda8104c8d5cad` に一致する。
- branch は `redesign/single-writer`、作業場所は専用 worktree `D:/csharp/Quiver-sw` であり、未コミットの実装差分と `develop` 未マージ commit はない。
- 正本 §2.1〜§2.5、§4.1〜§4.6、§5.1〜§5.5、§7.1、§7.6〜§7.7、§8.2〜§8.4、§9 Wave 9、§10.3〜§10.4、§11.2、§13〜§16 と review C-5、C-6、M-1、M-5〜M-7、m-4 の境界が一致する。
- review C-5 は physical Sequence を内部 address に限定し、logical mutation、RAG、maintenance の public/logical output が full typed ID を保持する設計である。
- review C-6 は relationship raw entry が残る間の no-reuse を Wave 1、完全な再利用解放を本 Wave の `RelationshipReuseCoordinator` に分ける設計である。
  review 表記は設計上の Critical 解決と本 Wave の実装 gate を分離し、SKILL.md §0 の着手条件と整合済みである。
- review M-1 は vacuum 経由の実 slot reuse と Generation 増加を本 Wave に割り当てている。
- review M-5 は sidecar migration history の廃止を本 Wave に割り当て、Hosting、Rag、SourceGen 等の addon を新 API へ追従させる。
- review M-6 は horizon 上で到達可能な property version だけを payload corruption 判定対象とし、property version と専有 payload を同じ write transaction で回収する設計である。
- review M-7 は full-text の未参照 orphan と reader horizon を越えた旧 artifact の物理回収を本 Wave に残している。
- review m-4 は reader を強制失効せず、active snapshot count、oldest age、開始位置を診断と警告へ公開する設計である。
- Wave 1 の relationship no-reuse、Wave 3 の owner-bound property/vector payload、Wave 4 の `WriterLease` / `SnapshotRegistry`、Wave 5 の strict commit/recovery、Wave 6 の unified scalar index、Wave 7/8 の versioned segment manifest と durable artifact が実在することを実装と回帰 test で確認する。
- 現行 `Vacuum` の active transaction 0 件待ち、sidecar `migrations.history`、Sequence だけを replay map key にする logical mutation、Hosting の `LockTimeout`、score を捨てる `RagHit` は本 Waveで target contract へ置換する入力であり、compatibility surface として残さない。
- 本書の目標状態と検証計画についてユーザの着手承認を得ており、review 表記の整合とともに doc-only commit として push する。

## 2. 読む順序

1. 正本 §9 Wave 9。
2. 正本 §2.1〜§2.5、§4.1〜§4.6、§5.1〜§5.5。
3. 正本 §7.1、§7.6〜§7.7、§8.2〜§8.4。
4. 正本 §10.3〜§10.4、§11.2、§13〜§16。
5. review C-5、C-6、M-1、M-5〜M-7、m-4。
6. `docs/spec/00_overview.md`、`docs/spec/01_storage_paging.md`、`docs/spec/02_wal_recovery.md`、`docs/spec/03_mvcc.md`、`docs/spec/04_records_index.md`、`docs/spec/05_query.md`、`docs/spec/06_vector.md`、`docs/spec/07_fulltext.md`、`docs/spec/08_known_limits.md`、`docs/design/development.md`。

## 3. 目標状態と検証計画

Wave 9 は primary/derived data の回収、logical durability、migration、診断、RAG addon を、同じ writer lease と snapshot horizon に従う一つの運用 contract へ変更する。

- **horizon-aware vacuum contract**: vacuum は通常の write operation と同じ `WriterLease` を取得するが、reader 0 件を待たない。
  `SnapshotRegistry` の oldest committed high-water から安全な horizon を固定し、その horizon より新しい version、manifest、artifact、slot を回収しない。
  long reader が存在しても安全な horizon まで前進し、reader 終了後の次回実行で残りの回収が進む。
- **primary/derived GC ordering contract**: horizon を越えた scalar entry、全文/vector segment、adjacency/co-membership view 等の derived data を先に無効化または回収する。
  次に property version とそれだけが参照する blob/vector payload を同じ write transaction で回収し、その後に incidence と entity slot を回収する。
  horizon 上で到達可能な property version が payload を失う場合だけ primary corruption とし、回収済み version と stale derived ref は corruption にしない。
- **relationship reuse contract**: `RelationshipReuseCoordinator` は回収候補ごとに reader horizon 通過、adjacency base rebuild、delta/epoch reset、locator rebuild、derived durable の完了を durable phase state で記録し、その順序を飛び越えない。
  全 phase の durable 完了後だけ relationship Sequence を free list へ release し、次の create で Generation を進めて再利用する。
  release 前の crash は safe leak とし、reopen 後に最後の durable phase から再開する。
  release 後の旧 raw base/delta/locator/epoch entry が新 Generation の relationship へ retarget しない。
- **segment GC contract**: full-text/vector の commit 前 artifact orphan、publish 後に参照されなくなった旧 manifest/body、rebuild で置換された artifact を区別する。
  未参照 orphan は committed manifest 集合との照合後に回収し、旧 reader が参照し得る manifest/body は reader horizon 通過まで保持する。
  GC の中断または crash で current manifest が指す body を削除せず、reopen 後に同じ search result と rebuild stateを復元する。
- **auto-vacuum contract**: `AutoVacuumWorker` は active transaction count を理由に no-op せず、writer contention policy に従って lease を取得し、取得後に固定した horizon まで一回分を実行する。
  tick は逐次化し、停止、例外、timeout、database faulted の各経路で worker と writer lease を leak しない。
  report は採用 horizon、各 store/artifact の回収件数、safe leak/保留件数、再利用 release 件数を診断可能にする。
- **transactional migration history contract**: migration history を sidecar text file から primary schema catalog 内の versioned store へ移す。
  migration の schema/data mutation と history entry は同じ write transaction の strict `Commit` で可視化し、commit 後の sidecar append hook を持たない。
  rollback、commit 前 crash、reopen、重複 ID、順序付けで、適用済み data/schema と history が食い違わない。
  migration は logical schema/data migration のままであり、旧 database format の physical migration を提供しない。
- **logical mutation contract**: logical record は owner-bound property address、full typed owner identity、Single/Set mutation、vector property valueを表現する。
  replay map は source ID の packed identity 全体を key にし、same-sequence/different-generation を同一 owner として扱わない。
  durable commit 完了後の batch だけを sink へ渡し、abort、commit failure、recovery loser の mutation を発行しない。
  replay は Vertex、Edge、Nexus の create/delete/property set/add/remove と vector valueを新 ID へ再マップし、旧 logical record compatibility は持たない。
- **diagnostics and hosting contract**: lock/deadlock 名の option、configuration key、metric、EventSource counter、sample を削除する。
  Hosting は `WriterWaitTimeout` と `WriterContentionMode` を bind し、旧 key fallback を持たない。
  OTel/EventSource は writer wait duration/count、active snapshot count、oldest snapshot age、rebuild/GC state を安定した新 metric 名で公開し、`SnapshotRegistry` の開始位置を診断 API と警告へ渡す。
- **RAG score contract**: `RagHit` は融合前の BM25 score、vector similarity、融合後 score、融合方式と定数を保持する。
  text-only、vector-only、hybrid、candidate 制約付き hybrid の各経路で値の意味を一貫させ、隣接 chunk merge は代表 hit の score 内訳を失わない。
  Rag 層で独自に順位だけを再融合する経路を残さず、engine operator の score result を利用者 surface まで伝播する。
- **RAG push-down contract**: owner candidate set または predicate を scalar、full-text、vector の top-k 前へ渡す。
  後段 filter と oversampling を正しさの前提にせず、candidate 母集団内の strict result と同じ top-k を返す。
  filtered text/vector/hybrid は同じ read transaction の snapshot と full typed ID を保持し、raw Sequence を logical identity にしない。
- **RAG replacement lifecycle contract**: 内容変更による管理 Vertex の置換は旧 ID と新 ID の対応を結果で返す。
  ID 維持を保証せず、旧管理 Vertex に付いた Quiver 管理 Edge、利用者 Edge、参加 Nexus を同じ logical delete 境界で連鎖削除する。
  利用者が対応表を使って必要な関係を明示的に再アンカーでき、旧 ID の関係を新 ID へ暗黙継承しない。
- **addon convergence contract**: `Quiver.Hosting`、`Quiver.OpenTelemetry`、`Quiver.Rag`、`Quiver.Embedding`、SourceGen、Studio、MCP、samples が新 option、transaction、property、index、score、replacement contractだけを参照する。
  旧 lock/deadlock option、sidecar migration path、旧 logical property形状、scoreを持たないRAG resultをactive surfaceに残さない。
- **補修方法**: 最初に maintenance/reuse/segment GC、migration history、logical mutation、diagnostics/Hosting、RAG score/push-down/replacement、addon call site を target contractへ一括変更する。
  続けて solution build の compiler error を旧 option、sidecar history、Sequence-only replay、score-less result、addon drift の不足一覧として補修する。
  build 成功後は maintenance ordering、crash/reopen、migration、logical replay、Hosting/telemetry、RAG、SourceGen/Studio/sample、PublicApi、solution test の順で契約漏れを補修する。
- **契約保証**: long reader、reuse phase ordering、各 phase crash/reopen、payload/property atomic GC、segment orphan/old-reader GC、migration rollback/reopen、logical commit/replay、configuration binding、metric names、score diagnostics、candidate push-down recall、replacement mapping、Edge/Nexus cascade を回帰 testへ追加または更新する。
  as-built は horizon-aware maintenance、transactional history、logical stream、diagnostics、RAG/addon contractを実装済みとして更新する。

## 4. 本 Wave 固有の落とし穴

- vacuum 開始条件を active transaction 0 件のままにしない。
  writer は lease で排他するが、reader は horizon の入力であり、終了待ちの理由ではない。
- oldest reader の開始時 high-water より新しい record を、vacuum 開始後の latest state を見て回収しない。
  一回の実行中は固定した horizon を使う。
- property version と専有 payload を別 commit で回収しない。
  その crash 窓は到達可能な primary ref の dangling を作る。
- relationship record を heap から除去しただけで Sequence を free list へ戻さない。
  raw base/delta/locator/epoch entry が残る間の release は ABA を起こす。
- coordinator phase を process-local flag だけで管理しない。
  crash 後に安全に再開できる durable state と冪等な phase operation が必要である。
- derived durable の前に free release しない。
  release 前 crash は容量を一時的に失う safe leak でよく、alias を作る unsafe reuse より優先する。
- old manifest/body を current manifest から外れた直後に削除しない。
  publish 前に開始した reader が旧 artifact を参照できるため、reader horizon を越えるまで保持する。
- orphan artifact の判定をファイル時刻だけに依存させない。
  committed manifest と durable prepare/publish state を照合し、current artifact を誤削除しない。
- migration の data/schema commit 後に別 sidecarへ history を appendしない。
  二つの durable pointが残ると crash 後の再適用と重複 mutation を避けられない。
- logical replay map を `Sequence` だけで key 化しない。
  Generation の違う source identityを同一 entityとして再生してしまう。
- vector property の logical record に物理 `VectorPayloadRef` を外部契約として焼かない。
  replay 先で意味を持つ semantic vector valueを記録し、target側のpayload allocationに任せる。
- writer wait と frame latch の `Lock` を混同しない。
  削除対象は旧transaction lock/deadlock public contractであり、物理メモリ安全性の短時間 latchは残る。
- metric 名の alias または旧 configuration key fallback を残さない。
  本トラックは clean breakであり、active docsとsamplesも新名へ同時に切り替える。
- RAG candidate filter を top-k 後の削除として実装しない。
  候補外 hit のために母集団内の正解が押し出される recall hole が残る。
- RAG score を順位から逆算しない。
  BM25、vector similarity、fusion scoreと定数は各operatorが計算した値をそのまま伝播する。
- 管理 Vertex 置換で旧 ID の利用者 Edge/Nexusを新 IDへ暗黙継承しない。
  旧関係は連鎖削除し、再アンカーは返却した対応表を使う利用者の明示操作にする。
- addon の build成功だけで完了にしない。
  public configuration、metric名、score値、candidate recall、replacement lifecycleをtestで固定する。

## 5. 完了確認

| gate | 適用/N/A | コマンドまたは差分根拠 | 合格条件 |
|---|---|---|---|
| 機能 test | 適用 | `dotnet build Quiver.slnx -v minimal`、`dotnet test tests/Quiver.Transactions.Tests/Quiver.Transactions.Tests.csproj --no-build`、`dotnet test tests/Quiver.Stores.Tests/Quiver.Stores.Tests.csproj --no-build`、`dotnet test tests/Quiver.Index.Tests/Quiver.Index.Tests.csproj --no-build`、`dotnet test tests/Quiver.Operators.Tests/Quiver.Operators.Tests.csproj --no-build`、`dotnet test tests/Quiver.Tests/Quiver.Tests.csproj --no-build`、`dotnet test tests/Quiver.Backend.Tests/Quiver.Backend.Tests.csproj --no-build`、`dotnet test tests/Quiver.Hosting.Tests/Quiver.Hosting.Tests.csproj --no-build`、`dotnet test tests/Quiver.Rag.Tests/Quiver.Rag.Tests.csproj --no-build`、`dotnet test tests/Quiver.SourceGen.Tests/Quiver.SourceGen.Tests.csproj --no-build`、`dotnet test tests/Quiver.PublicApi.Tests/Quiver.PublicApi.Tests.csproj --no-build`、solution 全 test project | 0 errors、0 warnings、全対象 test成功。readerを待たないvacuum、ordered reuse、transactional history、logical replay、new diagnostics/Hosting、RAG score/push-down/replacement、addon convergenceがbinaryとin-memoryの適用境界で成立する |
| crash test | 適用 | binary backendでrelationship coordinatorの各durable phase、property/payload GC、full-text/vector artifact orphan/retire、migration data/schema/history commitの各境界をfault injectionし、`dotnet test tests/Quiver.Backend.Tests/Quiver.Backend.Tests.csproj --no-build` と `dotnet test tests/Quiver.Tests/Quiver.Tests.csproj --no-build` を実行する | release前crashはsafe leakとしてreopen後に再開し、release後の旧raw entryは新Generationへretargetしない。到達可能なproperty refはpayloadを失わず、current/old-reader manifest bodyを誤削除しない。migration stateとhistoryは両方commitまたは両方rollbackになる |
| baseline gate | 適用 | long readerを保持したvacuum correctness runner/test、reader終了後の再実行、auto-vacuumとwriter contention、relationship reuse前後を同一commitで測定し、環境、コマンド、生結果を`docs/benchmarks/`へ保存する | long readerのsnapshot resultが不変で、vacuumはreaderを待たず安全なhorizonまで完了する。reader終了後の次回実行で回収とreuseが前進し、旧IDはnot found、新IDはGeneration増加となる。correctness gateのため旧throughput比を合否根拠にしない |
| as-built 更新 | 適用 | `docs/spec/00_overview.md`、`docs/spec/01_storage_paging.md`、`docs/spec/02_wal_recovery.md`、`docs/spec/03_mvcc.md`、`docs/spec/04_records_index.md`、`docs/spec/05_query.md`、`docs/spec/06_vector.md`、`docs/spec/07_fulltext.md`、`docs/spec/08_known_limits.md`、`docs/design/development.md`、README、Hosting/Observability/Migration/RAG samplesのactive contract scan | reader 0件待ちvacuum、immediate relationship reuse、sidecar migration history、旧lock/deadlock option/metric、scoreを持たないRAG hit、後段filter前提の記述が残らず、実装済みcontractと一致する |

追加条件は次のとおりである。

- `RelationshipReuseCoordinator` は horizon、base rebuild、delta/epoch reset、locator rebuild、derived durable、free release の順序を durable state と test で固定する。
- coordinator の各 phase は冪等で、各境界 crash/reopen 後に release 前 safe leak または完了状態へ収束する。
- relationship Sequence の実 reuse は Generation を進め、旧 full typed ID と旧 raw entryが新 relationshipへaliasしない。
- long reader中のvacuumはreaderの終了を待たず安全なhorizonまで進み、そのsnapshot resultを変えない。
- property versionとそれだけが参照するblob/vector payloadは同じwrite transactionで回収され、到達可能なprimary refがdanglingにならない。
- full-text/vectorのcurrent manifest、old readerが参照可能なmanifest/body、未参照orphanを区別し、segment GC後のreopen/searchが同じvisible resultを返す。
- migration historyはprimary schema catalogからreopen後に復元され、`migrations.history` sidecarの読書きとfallbackがproduction source、tests、samples、active docsに0件である。
- migrationのdata/schema mutationとhistory entryは同じcommit境界にあり、rollback、commit前crash、reopen、重複ID、順序testが成功する。
- logical mutationはfull typed source identity、owner-bound property address、Single/Set、vector valueを保持し、abortまたはcommit失敗batchをsinkへ渡さない。
- logical replayはsame-sequence/different-generationを区別し、Vertex、Edge、Nexusと全property mutationをtarget IDへ再マップする。
- Hosting configurationと`QuiverDatabaseOptions`は`WriterWaitTimeout` / `WriterContentionMode`を公開し、旧lock/deadlock key、option、fallbackが0件である。
- OTel/EventSource/diagnosticsはwriter wait、active snapshot、oldest snapshot、rebuild/GCを新metric名で公開し、旧lock/deadlock metric名が0件である。
- `RagHit`の新規public score/fusion surfaceには日本語XML docがあり、PublicApi approvalは最終contractだけを含む。
- text-only、vector-only、hybrid、filtered hybridのRAG hitがBM25、vector similarity、fusion score、fusion method/constantsを意味どおり返す。
- candidate setまたはpredicateはscalar、full-text、vectorのtop-k前へ渡され、母集団内strict resultとのrecall/top-k一致testが成功する。
- RAG管理Vertexの置換結果は旧IDと新IDの対応を返し、旧IDの利用者Edgeと参加Nexusを連鎖削除し、新IDへ暗黙継承しない。
- `Quiver.Hosting`、`Quiver.OpenTelemetry`、`Quiver.Rag`、`Quiver.Embedding`、SourceGen、Studio、MCP、samplesが新APIだけでbuild/testされる。
- production source、tests、benchmarks、samples、active docsに内部管理表記の新規漏出がなく、staged pathに対する`scripts/agent-guardrails/check-track-markers.ps1`と`git diff --check`が成功する。
- branch tipは完成または検証済み補修commitであり、topic branchへpush済みである。

merge と tag はユーザの明示承認を別々に得る。
