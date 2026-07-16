# Wave 2 着手指示書: domain vocabulary と新 database/WAL format foundation

> 効力宣言: 本書と設計正本が食い違う場合は設計正本を優先し、食い違いをユーザへ報告する。
> 作成日: 2026-07-15
> 対応する正本のバージョン: `3f3d63bcfee2b92c890d3000907cbd1891427d7e`
> ステータス: 実装・検証完了(2026-07-15)

## 1. 着手前チェック

- ローカル annotated tag `redesign-wave-1` が merge commit `7b491c0d30c4b65fe2ca0820215c17686d02fb2b` に存在する。
- branch は `redesign/single-writer`、作業場所は専用 worktree である。
- topic と local `develop` は Wave 1 merge commit を含み、未コミットのコード変更がない。
- 正本 §0.1、§6、§8.1、§9 Wave 2、§15、§16 の vocabulary、format、allocation 規則が一致する。
- review C-1 の no-steal/redo-only、M-1 の Wave 境界、M-5 の破壊的変更一覧が正本で解消済みである。
- 本書の目標状態と検証計画についてユーザの着手承認を得る。

## 2. 読む順序

1. 正本 §9 Wave 2。
2. 正本 §0.1、§6、§8.1。
3. 正本 §7.1、§7.2、§7.4。
4. 正本 §15、§16 の vocabulary、allocation、WAL 決定。
5. review C-1、M-1、M-5。
6. `docs/design/00_conventions.md` の public API、file、test 規約。

## 3. 目標状態と検証計画

Wave 2 は vocabulary、database format、WAL format、file allocation を別タスクへ分解せず、solution 全体を一つの target state へ変更する。

- **あるべき姿**：Quiver の graph domain entity は Vertex、Edge、Nexus と呼び、owner に束縛された versioned value は Property と呼ぶ。database facade は `QuiverDatabase` と呼ぶ。旧名の型、method、file、generated API、serialization field、telemetry field、sample、public docs、互換 alias は存在しない。database と WAL は `QUIVER-SW` family の一形式だけを読み書きし、winner は明示 Commit だけで決まる。新規 DB は既定 1 MiB で開始し、増分上限 64 MiB の適応成長を使う。
- **一括変更範囲**：Core identity、facade、backend、transaction、stores、operators、query/client DSL、logical mutation、RAG/Embedding/Hosting/OTel、Source Generator、Studio、samples、benchmarks、tests、PublicApi approval、active docs、database/WAL codec、recovery parser、PagedFile option plumbing。
- **補修方法**：最初に domain identifier と file 名を全対象へ一括変更し、旧名 alias は作らない。続けて format/WAL/allocation を target contract へ置換する。その後に solution build の compile error を不足 call site の一覧として解消し、focused test、全 test、crash/fuzz、performance の順に契約漏れを補修する。
- **契約保証**：PublicApi と Source Generator golden は新語彙だけを承認する。旧 DB/WAL と旧 serialized field は明示 mismatch となる。allocation test は初期容量、倍増列、増分上限、reopen、checksum、データ保持を保証する。active root の vocabulary scan は旧 graph domain identifier と shim を拒否する。

## 4. 本 Wave 固有の落とし穴

- `Node` の機械置換を graph entity 以外へ広げない。B-tree node、syntax node、HNSW node、linked-list node の一般用語は維持する。
- `Graph` 全体を `Quiver` へ置換しない。`GraphTraversal`、graph algorithm、`IGraphVertex` は維持し、`GraphDatabase` だけを `QuiverDatabase` へ変える。
- Property は名称を変えず、Wave 3 まで残る `PropertyId` の削除時期も動かさない。
- ID の packed bit layout と `EntityKind` の underlying value は変えない。enum member 名だけを Vertex、Edge、Nexus にする。
- 旧名を新名へ転送する alias、partial wrapper、extension method、JSON fallback、Source Generator の二重生成を作らない。
- rename 後の compile success だけでは完了しない。reflection、generated source、approved API、diagnostic string、telemetry、sample command、file 名を旧語彙 scan で検査する。
- WAL parser skeleton は winner/loser 判定までを担当し、commit/checkpoint/recovery の統合実装を Wave 5 から前倒ししない。

## 5. 完了確認

| gate | 適用/N/A | コマンドまたは差分根拠 | 合格条件 |
|---|---|---|---|
| 機能 test | 適用 | `dotnet build Quiver.slnx`、全 test project、PublicApi、SourceGen、sample/tool build | 0 errors、0 warnings、全 test 成功。新語彙だけを公開する |
| crash test | 適用 | Storage/WAL/Transactions/Backend の corruption、truncation、old fixture rejection、fuzz、process-kill parser test | torn/unknown/old input を誤って winner または新 DB として受理しない |
| baseline gate | 適用 | baseline と同じ `--basic-perf`、database create/open と file growth の focused measurement | CRUD、visibility、traversal の各 p50 が baseline 比 1.20x 以内。成長回数が規定列と一致する |
| as-built 更新 | 適用 | `docs/spec/`、`docs/design/development.md`、README、operations、samples の vocabulary/format/allocation audit | target vocabulary、新 format、1 MiB 始動の適応成長が実装済み contract と一致する |

追加条件は次のとおりである。

- public identity は `VertexId`、`EdgeId`、`NexusId` だけを graph entity ID として公開し、Property の名称は維持する。
- `EntityKind` は Vertex、Edge、Nexus だけを持ち、underlying value と packed layout は Wave 1 の値を維持する。
- database facade は `QuiverDatabase` と `QuiverDatabaseOptions` だけを公開する。
- CRUD と traversal は `CreateVertex`、`CreateEdge`、`CreateNexus`、`Vertices`、`Edges`、`Nexuses` と対応する新語彙だけを公開する。
- store、operator、query slot、logical mutation、telemetry、Source Generator、Studio、sample、test file の domain identifier が新語彙へ揃う。
- active source と public docs に旧 graph domain identifier、alias、obsolete shim、serialized-field fallback がない。
- graph entity 以外の algorithm/data-structure node は誤って Vertex へ変更されていない。
- `QUIVER-SW` database/WAL magic と family version だけを生成する。
- WAL winner/loser は明示 Commit だけで分類され、unknown、truncated、checksum corruption、旧 record を拒否する。
- `QuiverDatabaseOptions.InitialFileAllocationBytes` の既定値は 1 MiB、`MaximumFileGrowthStepBytes` の既定値は 64 MiB である。
- 新規 DB と reopen 後の拡張が 8 KB alignment、適応増分、checksum、既存データ保持を満たす。
- branch tip は完成または検証済み補修 commit であり、ローカル commit まで作成し、push は行わない。

merge と tag はユーザの明示承認を別々に得る。

## 6. 完了記録

solution buildは0 warning、0 errorで成功した。
全test projectは成功し、`Quiver.Tests`の意図的なskip 9件を除く失敗は0件だった。
Public API approval、Source Generator、WAL、Storage、Transactions、Backend、Fuzz、Studioとsample/tool buildを含む各gateが成功した。

`--basic-perf`はbaselineと同じNIRVANA、.NET 10.0.9で実行した。
CreateVertexは1.009x、property付きCreateVertexは0.981x、CreateEdgeは0.985x、1-hopは最大1.007x、BFS 2-hopは0.961xであり、すべて1.20x以内だった。
このrunnerはp50ではなくbest-of-Nを記録するため、指示書のp50条件は同一runnerの比較可能値で判定した。

旧domain API、旧serialized field、旧WAL record、旧file名のactive root監査は0件だった。
日本語の旧domain語彙は、B-tree、HNSW、wait-for graphの一般用語として意図的に維持するNodeだけが残った。
`check-track-markers.ps1 -Scan`と`git diff --check`は成功した。
