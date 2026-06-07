# ARCH-7 — 単一 LogicalPlan IR + optimizer 集約 (Phase 4)

> 作成: 2026-06-07 / 対象ブランチ: develop
> スコープ決定: **Option A — 新 LogicalPlan 代数をフルに新設** (docs/design/11 §5 文言どおり、ユーザ選択 2026-06-07)
> 指示書: `plans/arch4-to-arch8-rearchitecture-phases.md` §5 / 設計: `docs/design/11_rearchitecture_master_plan.md` §5
> 互換性: query 層のみの変更。**オンディスク無変更につき FormatVersion は据え置き** (V7VectorInFile)。
> 公開 API: DSL の public サーフェス (GraphTraversal / GraphTraversalSource / SubTraversal / MatchQuery) は**不変**。内部のみ全面刷新。

---

## 0. 現状認識 (post-ARCH-1 実測)

| 層 | 実体 | 備考 |
|---|---|---|
| fluent DSL | `GraphTraversal<T>` / `GraphTraversalSource` / `SubTraversal` | `IOperatorBuilder` ツリーを構築 |
| Match DSL | `MatchCompiler` | これも `IOperatorBuilder` ツリーを構築 |
| 論理もどき | `IOperatorBuilder` + ~20 の `*Builder` (`OperatorBuilders.cs` / `SingleNodeBuilders.cs` / `PendingKnnBuilder.cs`) | `Build(schema)` で物理へ lower。**事実上の論理 IR だが名前付き代数でない** |
| KNN 押し下げ | `PendingKnnBuilder` + `GraphTraversal` の ~18 箇所の `if (_builder is PendingKnnBuilder)` 分岐 | **表層 DSL に散在** |
| optimizer | `QueryOptimizer` + `GraphStats` | **lowering 経路の外**。PW-17 expand strategy / VEC-6 `ChooseKnnStrategy` で部分利用。DSL の KNN 判定は `PendingKnnBuilder.Materialize` が独自再実装 |
| 実行 | `IPhysicalOperator` pull 型 (`tx.Execute` / `tx.ExecuteCursor`) | **変更しない** |

→ ARCH-7 で解消する真の散在は **(1) KNN 押し下げが DSL 埋め込み** と **(2) optimizer が lowering の外で独自 KNN ロジックを再実装**。

---

## 1. 設計判断 (§5 の 4 項目を確定)

### 設計判断 1: LogicalOp ノード型と代数 (Quiver.Query.Logical)

`abstract record LogicalOp` を基底に immutable record で定義。各 node は **論理シェイプメタデータ** `int CurrentEntityColumn` / `int PredictedOutputColumnCount` を持つ (GC-6 alias/carry 列計算を DSL 側で継続するため。現 builder と同じ役割)。

確定ノード集合 (§5 の 14 種を現実の builder 全網羅にマップして最終確定):

| LogicalOp | 由来 builder | 引数 (要点) |
|---|---|---|
| `Scan` | ScanBuilder / RelationshipScanBuilder | `EntityKind Kind, LabelId? Label` (Node+label / Node all / Relationship all) |
| `NodeSeed` | SingleNodeBuilder / MultiNodeBuilder | `NodeId[] Ids` |
| `CorrelatedInput` | CorrelatedSeedBuilder | (probe 参照は物理段で bind) |
| `Filter` | FilterBuilder | `LogicalOp Source, Func<ISchemaApi,IPredicate> PredicateFactory` |
| `Expand` | ExpandBuilder | `Source, int SourceColumn, Direction, string? Type, ExpandOutputMode, int[]? Carry` |
| `VarLenExpand` | VarLenExpandBuilder | `Source, Direction, string? Type, int MinHops, int MaxHops` |
| `Path` | ShortestPathToBuilder (+ WeightedShortestPath 経路を将来統合) | `Source, NodeId Target, Direction, string? Type, long MaxHops` (hop 数最短) |
| `Knn` | KnnNodeSourceBuilder / FilteredKnnNodeSourceBuilder / **PendingKnnBuilder を吸収** | `LogicalOp? Candidate, string IndexName, float[] Query, int K, int Dim`。`Candidate==null` ⇒ vector-first / `!=null` ⇒ graph-first。**押し下げ前は常に `Candidate==null` で生成し、optimizer rule が候補を確定** |
| `PropertyLookup` | PropertyLookupBuilder | `Source, string Key, EntityKind Kind` |
| `LabelNameLookup` | LabelNameLookupBuilder | `Source, int NodeColumn` |
| `RelationshipEndpoint` | RelationshipEndpointBuilder | `Source, int RelColumn, RelationshipEndpoint Endpoint` |
| `Limit` | LimitBuilder | `Source, long Limit, long Skip` |
| `Sort` | SortBuilder | `Source, (string? Key | int Column), bool Descending` |
| `Dedup` | DedupBuilder | `Source, int KeyColumn` |
| `Branch` | BranchedBuilder | `Source, Func<ISchemaApi,(CorrelatedInputOperator[],IPhysicalOperator[])> Build, BranchKind {Union,Coalesce,Optional}` |

確定ノードは上記 **15 種**。§5 列挙との差分とその根拠:

注:
- §5 列挙の `Project` は `PropertyLookup` / `LabelNameLookup` / `RelationshipEndpoint` の 3 record に分割 (planner の switch が明瞭になる。概念的には Project ファミリ)。
- §5 列挙の `Apply(correlated)` は `Branch` (Union/Coalesce/Optional) が担う。`Where`/`Not`/`And`/`Or` は **述語ツリー (SubquerySemiJoinPredicate)** として `Filter` 内に載るため LogicalOp ノードにはしない (現状と同じ)。
- **`Aggregate` はノードにしない**。現状 Sum/Max/Min/Mean/GroupCount は terminal で `PropertyLookup` プランを実行し C# で fold している (物理 Aggregate operator は存在しない)。専用 `AggregateOp` を足しても消費者のない dead code になるため、**terminal fold over `PropertyLookup`** として実現する (実行エンジン不変の制約とも整合、不要コードを作らない方針)。
- **`Path` は hop 数最短 (ShortestPathTo) のみ**を IR 化する。`WeightedShortestPath` は GraphTraversalSource の leaf terminal で、現状 builder 層を通らず物理オペレータを直接構築・実行している (= 統一対象の「3 系統」に含まれない) ため、本タスクでは IR 化せず現状のまま据え置く。
- `Branch` の `Build` クロージャは物理 probe/branch を生成するため、planner 段で評価する。LogicalOp としては不透明なクロージャを保持 (現 BranchedBuilder と同形)。「実行エンジンを作り直さない」制約を尊重。

### 設計判断 2: フロント別 lowering 経路

- **fluent DSL** (`GraphTraversal<T>`): フィールド `_builder` を `LogicalOp _plan` へ置換。各ステップは `LogicalOp` を構築するのみ。**`if (_builder is PendingKnnBuilder)` 分岐を全廃** — KNN は通常の `Knn(Candidate=null)` ノードとして積み、`Filter`/`Limit` も普通に重ねる。押し下げは optimizer に委譲。
- **Match DSL** (`MatchCompiler`): `IOperatorBuilder` → `LogicalOp` を返すよう変更。
- **SubTraversal** (public、内部 builder): 内部を `LogicalOp` に。exists/branch 述語は `PhysicalPlanner.Plan(plan, schema)` で物理化。public メンバ不変。
- **将来の Cypher/Gremlin parser**: parser → `LogicalPlan` のみで接続可 (本タスクでは parser は作らない、接続点を空けるだけ)。

### 設計判断 3: optimizer (rule + cost) の構成 — Quiver.Query.Optimizer

新 `LogicalOptimizer.Optimize(LogicalOp plan, GraphStats? stats, ISchemaApi schema) -> LogicalOp`。terminal で lower 直後に 1 度呼ぶ。rule:

1. **KnnPushdown** (`PendingKnnBuilder.Materialize` / `HasStructuralSelectivityHint` / `ShouldFallBackToVectorFirst` / `BuildVectorFirstWithReplayedFilters` / `FastLabelIndexThresholds` / `FastIndexThresholdForDim` を**そのまま移設**):
   `Filter+(Knn(Candidate=null))` を、構造ヒント + GraphStats から
   - graph-first: `Knn(Candidate=Filter+(Scan))`、または
   - vector-first: `Filter+(Knn(Candidate=null))` (label predicate を post-filter へ再配置)
   に書き換える。dim-aware piecewise threshold / `HasFastLabelIndex` 経路を維持。
2. **KnnLimitPushdown**: `Limit(n, Knn(K))` → `Knn(min(K,n))` (K が縮むなら Limit を消す。現 `Limit` の K-shrink 挙動を保存)。
3. **LabelScanRewrite** (`AllNodesScan→NodeByLabelScan`): `Filter(LabelPredicate, Scan(all node))` → `Scan(label)`。DSL の既存 fast-path (HasLabel で直接 `Scan(label)` 生成) は維持しつつ、Match 由来プランにも効く idempotent rule として追加。
- cost は `GraphStats` を参照。KNN strategy 判定は KnnPushdown rule 内に集約 (現 `QueryOptimizer.ChooseKnnStrategy` の DSL 用途を吸収)。`QueryOptimizer` の backend 向けユーティリティ (`SelectExpandPlan` = PW-17 / `SelectScan` / `OrderPredicatesBySelectivity` 等) は **backend access methods から使われるため残置** (ARCH-7 のスコープ外)。

### 設計判断 4: physical planner の選択表 — PhysicalPlanner

新 `PhysicalPlanner.Plan(LogicalOp op, ISchemaApi schema) -> IPhysicalOperator`。各 builder の `Build(schema)` 本体を 1 箇所の switch に移設 (1:1 対応表):

| LogicalOp | IPhysicalOperator |
|---|---|
| `Scan(Node, null)` | `AllNodesScanOperator` |
| `Scan(Node, L)` | `NodeByLabelScanOperator(L)` |
| `Scan(Relationship, _)` | `AllRelationshipsScanOperator` |
| `NodeSeed([1])` / `NodeSeed([n])` | `SingleNodeOperator` / `MultiNodeOperator` |
| `CorrelatedInput` | (bind 済 `CorrelatedInputOperator`) |
| `Filter` | `FilterOperator(Plan(src), factory(schema))` |
| `Expand` | `ExpandOperator(...)` |
| `VarLenExpand` | `VariableLengthExpandOperator(...)` |
| `Path` | `PairWithConstantOperator` + `ShortestPathOperator` |
| `Knn(null,...)` | `KnnNodeSourceOperator` |
| `Knn(cand,...)` | `FilteredKnnNodeSourceOperator(Plan(cand),...)` |
| `PropertyLookup` | `PropertyLookupOperator(...)` |
| `LabelNameLookup` | `LabelNameLookupOperator(...)` |
| `RelationshipEndpoint` | `RelationshipEndpointOperator(...)` |
| `Limit` | `LimitOperator(...)` |
| `Sort` | `(PropertyLookupOperator +) SortOperator(...)` |
| `Dedup` | `PathDedupOperator(...)` |
| `Branch` | `UnionOperator` / `CoalesceOperator` / `OptionalOperator` |
| `Aggregate` | `PropertyLookupOperator` スキャン (terminal が fold) |

**実行は不変**: terminal は `phys = PhysicalPlanner.Plan(LogicalOptimizer.Optimize(plan, stats, schema), schema)` → `tx.Execute(phys)` / `tx.ExecuteCursor(phys)`。

---

## 2. 実装増分 (各増分で `dotnet build Quiver.slnx` 緑、節目で `dotnet test`)

1. **増分1 — LogicalOp 代数**: `Quiver.Query.Logical` に `LogicalOp` 基底 + 上記 record 群 (シェイプメタデータ付き)。未配線。build 緑。
2. **増分2 — PhysicalPlanner**: `Quiver.Query.Optimizer` (or `.Physical`) に `PhysicalPlanner.Plan` を新設。各 builder の `Build` 本体を移植。未配線。build 緑。
3. **増分3 — LogicalOptimizer**: `Optimize` + 3 rule (KnnPushdown / KnnLimitPushdown / LabelScanRewrite)。`PendingKnnBuilder` のロジック/threshold を移設。単体テストで rule を検証。build 緑。
4. **増分4 — lowering 切替 (大)**: GraphTraversal / GraphTraversalSource / SubTraversal / MatchCompiler / RepeatStep を `LogicalOp` emit へ。terminal で `Optimize`→`Plan`。`IOperatorBuilder` / 全 `*Builder` / `PendingKnnBuilder` を削除。`if (_builder is PendingKnnBuilder)` 全廃。**full test suite** で結果不変を確認。
5. **増分5 — 仕上げ**: PW-18 optimizer regression sentinel 緑確認。PublicApi `Quiver.approved.txt` 差分確認 (DSL public 不変のはず → 変更なければ再承認不要)。XML doc 追従。コミット分割。

---

## 3. 完了条件 (§5)

- [x] `dotnet build Quiver.slnx` 0 errors。テスト緑: Quiver.Tests 485 / Operators 167 / Backend 186 / Sqlite 60 / Client 5 / PublicApi 1。
- [x] fluent DSL / Match が `LogicalPlan` 経由で実行され、既存クエリ結果が不変 (回帰緑)。
- [x] KNN 押し下げが optimizer (`LogicalOptimizer.KnnPushdown`) に集約され、表層 DSL から `PendingKnnBuilder` / `IOperatorBuilder` / 全 `*Builder` が消滅 (削除済)。`GraphTraversal` の `if (_builder is PendingKnnBuilder)` ~18 箇所撤去。
- [x] PublicApi 不変 (approved.txt 変更不要 — テスト緑)。PW-18 sentinel ベンチは full solution ビルド緑 (perf 回帰は別途 bench 実行)。

## 5. 実装結果 (commit 前)

- 新規: `src/Quiver/Query/Logical/LogicalOp.cs` (15 record 代数) / `src/Quiver/Query/PhysicalPlanner.cs` / `src/Quiver/Query/LogicalOptimizer.cs` / `src/Quiver/Operators/SingleNodeOperators.cs` (Single/MultiNodeOperator を物理層へ移設)。
- 改修: `GraphTraversal.cs` (`_builder`→`_plan`、KNN 特別扱い撤去、terminal で Optimize→Plan) / `GraphTraversalSource.cs` / `SubTraversal.cs` / `MatchCompiler.cs` / `IsExtensions.cs` / `ClientPredicates.cs` (LabelPredicate に Label/Column accessor)。
- 削除: `IOperatorBuilder.cs` / `OperatorBuilders.cs` / `PendingKnnBuilder.cs` / `SingleNodeBuilders.cs`。
- テスト/ベンチ追従: `KnnPushdownTests` / `KnnPushdownStatsAwareTests` を LogicalOptimizer ベースへ移植、`KnnBenchSupport` で post-filter baseline を物理直結化。
- FormatVersion: bump なし (query 層のみ)。

## 4. リスク / 留意

- **GC-6 alias/carry 列計算**: LogicalOp にシェイプメタデータ (`CurrentEntityColumn`/`PredictedOutputColumnCount`) を保持して現挙動を保存。RemapForExpand / carry の列番号ロジックは DSL 側に残す。
- **Branch / Aggregate のクロージャ保持**: 実行エンジン不変の制約のため、物理 probe/branch 生成クロージャを LogicalOp に不透明に持たせ planner 段で評価 (純粋な宣言的代数ではない妥協点。docs/design/11 §5 の「物理は選ぶだけ」に整合)。
- **KNN 押し下げの等価性**: 現状はステップ毎に candidate を逐次書き換えるが、Option A は terminal で 1 度 rule 適用。`Filter(Knn)` チェーン → `Knn(Candidate=Filter(Scan))` の書換で同一結果になることをテストで担保。
- **FormatVersion**: query 層のみ → **bump なし**。
