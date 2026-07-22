# Wave 10 着手指示書: legacy 除去・総合 gate・as-built 化

> 効力宣言: 本書と設計正本が食い違う場合は正本を優先し、食い違いをユーザに報告する。
> 作成日: 2026-07-22
> 対応する正本のバージョン: `402de8054e16147066613bfd666f0dd664c3d798`
> ステータス: 承認済み(2026-07-22)

## 1. 着手前チェック

- 前提 tag `redesign-wave-9` が存在し、未 tag の最小 Wave は Wave 10 である。
- 専用 worktree は `D:/csharp/Quiver-sw`、branch は `redesign/single-writer`、開始時 HEAD は `d76eebc28e7c33957485bcd54ad678ca6e2db58f` であり、未コミット差分と `develop` 未マージ commit はなかった。
- 正本 §2〜§14、review、Wave 0〜9 の指示書と tag を到達済み境界として照合する。過去 Wave の合格を根拠に残存 legacy を推定で無視せず、現行 tree と PublicApi を再監査する。
- review C-4、C-5、C-7 は設計、担当 Wave、検証方法が確定済みであり、Critical の設計着手状態と実装完了状態を分ける decision log を 2026-07-22 に追加した。実装完了は本 Wave の focused test、PublicApi、repo scan、Definition of Done で再証明する。
- review C-1〜C-8 に未解決の設計 Critical はない。Major/Minor の残存記述は正本へ反映済みか、Wave 10 の final audit で差分として扱えることを確認する。
- `.agents` と `.claude` の `quiver-implement/SKILL.md` は開始時点で byte-for-byte 一致する。これらは git 管理外であるため、変更が必要な場合だけメイン worktree 側の両 mirror を同時更新し、tracked commit へ混ぜない。
- 本書の目標状態と検証計画についてユーザの着手承認を得た後、本書を doc-only commit として topic branch へ push してからコード変更を開始する。

## 2. 読む順序

1. 正本 §9 Wave 10。
2. 正本 §7.1〜§7.7 の disposition 表と §8 の破壊的変更一覧。
3. 正本 §2〜§6 の不変条件、永続形式、処理フロー。
4. 正本 §10〜§14 の test/benchmark、docs、risk、Definition of Done。
5. 正本 §16 decision log と review C-1〜C-8、M-1〜M-7、m-1〜m-7。
6. Wave 0〜9 指示書の完了 gate、`plans/single-writer-redesign-baseline.md`、各 Wave の `docs/benchmarks/` 実測記録。
7. `docs/spec/00_overview.md`〜`docs/spec/08_known_limits.md`、`docs/design/development.md`、README、samples、tools、addon docs、PublicApi approval。

## 3. 目標状態と検証計画

Wave 10 は新しい互換層を追加する Wave ではない。Wave 0〜9 で到達した target contract を repo 全体の唯一の active contract とし、legacy、文書 drift、検証漏れを除去して as-built と証拠を固定する。

- **disposition convergence**: §7.1〜§7.7 の Delete/Rewrite/Move/Keep を production source、tests、benchmarks、samples、tools、addon、PublicApi へ一括適用する。dead source、旧 fixture、obsolete/alias/fallback、旧 benchmark runner、旧 approved API を残さない。Keep 対象の物理 latch、Sequence address、historical test fixture rejection、historical record は名前だけで削除対象にしない。
- **identity closure**: canonical Invalid と strict factory、Generation 込み equality、physical Sequence と logical full typed ID の境界を最終固定する。Generation 0 の typed ID は internal physical address にだけ存在し、public/query/traversal/index/full-text/vector/RAG/logical outputへ emit しない。same-sequence/different-generation、stale candidate、owner delete、reader horizon 後の reuse が別 entity へ alias しないことを全 access path で保証する。public raw-long candidate surface と未検証 Sequence の logical pipeline 流入を 0 件にする。
- **concurrency and durability closure**: facade/backend/manager の三境界で writer は一つ、reader は snapshot として並行する。SSN、Serializable、lock/deadlock、ReaderWriter、presume-committed、旧 WAL decoder、旧 checkpoint、ambient write context を active source/API/option/metric/test/runner から除去する。strict Commit、no-steal/redo-only、checkpoint、payload/ref/manifest、recovery winner/loser の crash contract を総合 matrix で再検証する。
- **primary and derived closure**: owner-bound property と immutable vector payloadを primary、scalar/full-text/vector/adjacency/co-membership を再構築可能な derived data として統一する。旧 Store、inline property、column/join optimization、adjacency V1、vector catalog/payload V1/V2、mutable postings/norms、FT 専用 WAL が残らない。drop/corrupt/rebuild、old/new snapshot、segment GC horizon、relationship reuse ordering を regression test で固定する。
- **test and benchmark convergence**: §10.1 の旧 test/runner を削除し、§10.2 の名称・配置へ統一し、§10.3 の contract/property/chaos coverage が実在する状態にする。テスト名と Assert が最終 contract を説明し、過渡的 API や legacy success fixture を固定しない。benchmark runner は §10.4 の gate に必要なものだけを active entry point として残す。
- **public/addon closure**: Quiver、Client、Embedding、Rag、Hosting、OpenTelemetry、SourceGen、Studio、MCP、samples が新 transaction、property、index、diagnostics、RAG score/push-down/replacement contract だけを参照する。PublicApi approval は final surface だけを含み、新規・変更 public API には日本語 XML doc を付ける。
- **as-built closure**: `docs/spec/` は target や将来予定ではなく実装済み構造、永続形式、snapshot、query、vector、full-text、既知制限を説明する。`docs/design/development.md` は理由と実装 map を集約し、README は利用者向け最小構成を維持する。サービス名や内部管理表記を公開 docs に出さない。
- **historical and skill closure**: historical record の commit hash、当時の API、実測値は改竄せず、現行入口からは superseded 表示と正本リンクで区別する。skill redirect の存在、非循環、root escape 防止と、`.agents` / `.claude` mirror の byte 一致を検証する。
- **baseline fixation**: crash、32 readers + writer、single writer commit、identity/visibility、relationship/nexus traversal、RAG ingest/write amplification、full-text、vector recall、segment publish stall、vacuum correctness を同一 commit・環境で再実行する。環境、commit、コマンド、生出力、判定を日付付き `docs/benchmarks/` へ保存し、正本 §10.4 の各上限を満たす。
- **補修方法**: 最初に repo inventory と検索結果から §7、§8、§10、§14 の未達を一つの差分集合へし、source/API/test/runner/docs を target contract へ一括変更する。次に solution build の compiler error を旧 call site と移動漏れの一覧にし、focused test、PublicApi、full test、Chaos/Fuzz、AOT、guardrail、benchmark の順で不足を補修する。互換 shim や一時 alias で build を通さない。
- **契約保証**: Definition of Done 18 項目を一件ずつ実装、test、scan、実測、as-built の証拠へ対応付ける。合格結果と追加・削除・改名した test/runner、baseline 生出力、N/A の差分根拠を本書へ追記してから merge 承認を求める。

## 4. 本 Wave 固有の落とし穴

- grep hit を機械的に削除しない。物理 page latch、internal Sequence、旧 format を拒否する test、decision log、historical plan は target contract 上も必要である。
- 過去 Wave tag を「現在も残存 0 件」の代用にしない。Wave 10 の tree と generated PublicApi を正本に対して再監査する。
- Generation 0 の internal physical address と public identity を混同しない。内部表現をすべて full ID 化して page arithmetic を壊さず、logical boundary の emit と caller input validation を閉じる。
- tests の大量の sequence-only fixture を一律に public compatibility と見なさない。internal store/codec の物理 fixture と public/query contract test を分け、後者だけを full typed ID へ揃える。
- historical record を削除・書換えしない。active docs と実装指示から到達したときに現行規範と誤認しないリンクを付ける。
- benchmark の絶対値を別環境の結果で判定しない。正本 baseline と同一 workload/runtime/seed が再現できない場合は同一セッション比較を取り、decision log とユーザ承認なしに gate を読み替えない。
- AOT publish、benchmark、test が生成する `artifacts/`、`BenchmarkDotNet.Artifacts/`、一時 DB、pack output を成果物へ混ぜない。tracked な生結果だけを明示 pathspec で stage する。
- full `check-track-markers.ps1 -Scan` は本 Wave の cleanup gate であり、既存 debt として無視しない。検出を単なる文字列置換で隠さず、公開 surface か historical/許可地かを判定する。
- `.agents` と `.claude` は gitignore 対象である。片側だけを編集せず、tracked commit の完了証拠とは別に byte 一致を報告する。
- solution build 成功だけで addon、AOT、crash、fuzz、performance、docs の完了としない。各 gate の独立した終了コードと判定を残す。

## 5. 完了確認

| gate | 適用/N/A | コマンドまたは差分根拠 | 合格条件 |
|---|---|---|---|
| 機能 test | 適用 | `dotnet build Quiver.slnx -c Release -v minimal`、`dotnet test Quiver.slnx -c Release --no-build`、`dotnet test tests/Quiver.PublicApi.Tests/Quiver.PublicApi.Tests.csproj -c Release --no-build`、`dotnet test tests/Quiver.FuzzTests/Quiver.FuzzTests.csproj -c Release --no-build`、focused identity/snapshot/property/index/segment/maintenance/RAG test | 0 errors、0 warnings、全 test 成功。§10 の削除・改名・追加と §14 Definition of Done が final contract を検証し、PublicApi approval に旧 surface がない |
| crash test | 適用 | `dotnet test tests/Quiver.Backend.Tests/Quiver.Backend.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~BinaryGraphStorageBackendCrashContractTests|Category=Chaos"`、`dotnet test tests/Quiver.Tests/Quiver.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~FullTextCrashContractTests|FullyQualifiedName~VectorSegmentSnapshotTests|FullyQualifiedName~FullTextSegmentSnapshotTests"`、checkpoint/truncate/payload/manifest/relationship reuse の fault-injection matrix | strict Commit を持つ winner だけが残り、loser は不可視。reopen は committed primary、payload/ref、manifestを復元し、derived欠落は rebuildへ収束する。safe leakは再開し、stale raw entryは再利用後のentityへretargetしない |
| baseline gate | 適用 | `dotnet run -c Release --project benchmarks/Quiver.Benchmarks -- --basic-perf`、`--read-scaling`、`--single-writer-perf`、`--clean-slate-page-wal-baseline 20 200 5000 20 1000 20`、`--clean-slate-csr-product-integration`、`--nexus-traversal`、`--clean-slate-segment-spike`、`--fulltext-segment-publish`、`--vector-segment-publish`、`--fts6`、`dotnet run -c Release --project benchmarks/Quiver.Benchmarks.RecallCheck`、vacuum correctness test/runner | §10.4 の各数値・correctness gateを満たす。32 readersはwriterを待たず、commit比1.5x以内、comparable p50は1.20x以内、durable point updateは3491.40us以内、2-hopは1.8982ms以下、nexus degree別は3.0x以内、FTは8.55ms/11.74x以内、vector recall@10は0.95以上、publish p99とvacuum correctnessが合格する。環境、commit、生出力を保存する |
| as-built 更新 | 適用 | `docs/spec/00_overview.md`〜`docs/spec/08_known_limits.md`、`docs/design/development.md`、README、samples/tools/addon docs、PublicApi、historical linkを実装とrepo scanへ照合する | target/予定形、旧 API/option/type/format、誤った実装 map がactive docsに残らず、正本 targetとas-builtの差分が0件。READMEは利用者向け最小構成、development docsは理由と実装mapを担う |

追加条件は次のとおりである。

- 正本 §14 Definition of Done 18 項目を、test、scan、実測、as-built のいずれかの具体的証拠へ一件ずつ対応付ける。
- review C-4 の canonical Invalid、strict factory、reserved/unknown kind rejection、raw unpack 境界が focused test と PublicApi で成立する。
- review C-5/C-7 の logical outputはfull typed IDを保持し、Generation 0をemitせず、same-sequence/different-generationとstale candidateを全access pathでreject/skipする。
- public `EntityCandidateSet`、direct raw-long filtered vector contract、未検証Sequenceをlogical query/traversalへ渡すactive surfaceが0件である。
- owner deleteとEdge/Nexus参照のlogical delete境界、reader horizon後のslot reuse、relationship reuse coordinatorの順序とnon-retargetがtestで成立する。
- production sourceにSSN、pstamp/sstamp、Serializable、transaction LockManager、ReaderWriter mode、DeadlockDetector、presume-committed、旧WAL decoderが0件である。
- database/WAL/adjacency/vector catalog/payloadはtargetの一形式だけを読み書きし、旧形式は明示 mismatch test以外にsuccess pathを持たない。
- owner-bound property、vector payload、scalar/full-text/vector index、segment manifest、vacuum/GC、logical mutation、migration、RAG/addonのcontractがreopenとsnapshotを跨いで成立する。
- §10.1 の旧 test/runnerが0件、§10.2の名称・配置が一致し、§10.3の追加coverageが実在する。
- `dotnet publish samples/Quiver.Samples.Crud/Quiver.Samples.Crud.csproj --configuration Release --runtime win-x64 --output artifacts/aot` が成功し、AOT/trim warning `IL2xxx` / `IL3xxx` が0件で、生成binaryが正常終了する。
- `scripts/verify-zero-dependency.ps1 -Configuration Release` が成功する。
- `scripts/agent-guardrails/check-track-markers.ps1 -Scan`、`scripts/agent-guardrails/check-markdown-links.ps1 -Roots README.md,docs`、`scripts/agent-guardrails/check-skill-redirects.ps1 -AgentsRoot D:/csharp/Quiver/.agents/skills -ClaudeRoot D:/csharp/Quiver/.claude/skills`、各 script の `-SelfTest`、`git diff --check` が成功する。
- `.agents/skills/quiver-implement/SKILL.md` と `.claude/skills/quiver-implement/SKILL.md` が byte-for-byte 一致し、historical redirect は存在、非循環、AgentsRoot 内に解決される。
- production source、tests、benchmarks、samples、tools、public docs の旧 API/option/type、obsolete shim、fallback、内部管理表記の意図しない参照が0件である。
- benchmark の測定環境、commit、実行コマンド、生出力、判定が日付付き `docs/benchmarks/` に保存され、新 baseline への参照がactive docsから到達できる。
- 完成・補修 commit はsolution buildと該当focused gate成功後だけに作り、明示pathspec、staged diff、guardrailを確認して即pushする。branch tipはWIPではない。

merge と tag はユーザの明示承認を別々に得る。

## 6. 実施結果

- 実装 commit: `d89c9fc8616a4bb04c21c608769b6f93b23d54b5`。
- §7 disposition: column cache、direct-array edge property join、group commit、旧開発 runner と対応する API/test/benchmark を削除した。Keep 対象の physical latch、Sequence address、historical rejection fixture は維持した。
- identity: CSR product integration の Generation 0 address を logical identity へ materialize し、identity/snapshot/single-writer/vector focused 57 tests と edge reuse/vacuum 33 tests が成功した。
- durability: writer lease、vector payload atomicity、WAL winner/loser、derived index rebuild の test を追加・再編し、crash/Chaos 151 tests と segment crash 16 tests が成功した。
- derived index format: full-text catalog と definition codec を current format 限定にし、旧 mutable postings metadata と decode fallback を削除した。旧 catalog/definition payload の rejection test が成功した。
- benchmark convergence: RecallCheck は current default M=32 / Mmax0=64 / efConstruction=400 の recall@10 0.95 gate のみを実行し、旧既定 scenario を削除した。
- test convergence: Release solution build は 0 warnings、0 errors、全 1,988 tests、PublicApi 1 test、Fuzz 32 tests が成功した。旧 test/runner の inventory は 0 件だった。
- addon/AOT: RAG 64 tests、zero-dependency 検査、Native AOT publish と生成 binary 実行が成功し、IL2xxx/IL3xxx warning は 0 件だった。
- audit: legacy scan、track-marker scan、Markdown link、skill redirect、各 self-test、mirror hash、`git diff --check` がすべて成功した。
- baseline: single writer 32 readers 比 0.773x、2-hop 1.7542 ms、durable edge update 1219.80 us、Nexus 最大 2.05x、full-text 7.683 ms / 2.01x、vector recall@10 1.000、full-text/vector publish p99 2.602 ms / 3.916 ms で全 gate に合格した。
- 生記録: `docs/benchmarks/2026-07-22_SingleWriterSnapshotReaders_Baseline.md`。
- Definition of Done §14 の18項目は、上記 source disposition、focused/full/crash/Fuzz test、PublicApi、AOT、repo scan、benchmark、as-built 文書に対応し、未達項目はない。
- merge と tag は未実施であり、規定どおり別々のユーザ承認を待つ。
