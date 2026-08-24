# クエリエンジン

> as-built 仕様（QUIVER-SW family version 2、2026-07-18）

## アーキテクチャ {#architecture}

クエリエンジンは **Volcano / iterator** モデルに従う。論理プランは最適化され、物理オペレータツリーへ
コンパイルされる。各オペレータは `GetNext()` を実装し、次の結果行を pull する。

## GraphKernel {#graph-kernel}

`GraphKernel` (`src/Yatagarasu/Operators/GraphKernel.cs`) は、コアとなるグラフ走査の抽象を提供する。
`IGraphAccessMethods.Expand()` をラップすることで、異なるストレージアクセスパスをまたいで
同一のアルゴリズム本体が動作する。

### Kernel コントラクト {#kernel-contract}

```csharp
interface IGraphKernel<TState>
{
    void Initialize(VertexId source, ref TState state);
    bool VisitNeighbor(VertexId source, VertexId target, EdgeId relId,
                       double weightRaw, int depth, ref TState state);
    bool ShouldContinue(int depth, TState state);
}
```

## 物理オペレータ {#physical-operators}

### スキャンオペレータ {#scan-ops}

| オペレータ | 説明 |
|---|---|
| `AllVerticesScanOperator` | 全ライブVertexのシーケンシャルスキャン |
| `VertexByLabelScanOperator` | スキャン中にラベルでフィルタ |
| `AllEdgesScanOperator` | 全Edgeのシーケンシャルスキャン |
| `VertexIndexSeekOperator` | 統一スカラ索引による等値検索と primary 再検証 |
| `VertexIndexRangeScanOperator` | 統一スカラ索引による範囲検索と primary 再検証 |

索引候補は `ScalarIndexDefinition`、`PropertyKeyId`、ラベルスコープを物理プランへ明示的に渡す。
演算子は索引 value が指すプロパティ版を現在の snapshot で読み、キー、値、所有者 identity、スコープを再検証する。
定義が `Ready` でない場合は primary property scan へフォールバックし、同じ比較と結果順序を保つ。

### Expand / 走査 {#expand-ops}

| オペレータ | 説明 |
|---|---|
| `ExpandOperator` | 単一ホップ展開 |
| `VariableLengthExpandOperator` | min/max depth 付きのマルチホップ |
| `BfsOperator` | 幅優先走査 |
| `ShortestPathOperator` | 重みなし最短経路 (BFS) |
| `WeightedShortestPathOperator` | Dijkstra ベースの重み付き最短経路 |
| `BidirectionalExpandOperator` | 経路探索のための双方向 BFS |
| `EdgeScanExpandOperator` | Edgeスキャンによる展開 |
| `EdgeEndpointOperator` | Edgeのエンドポイントを解決 |

### Nexus {#nexus-ops}

| オペレータ | 説明 |
|---|---|
| `AllNexusesScanOperator` | 全ライブNexusのシーケンシャルスキャン（型フィルタ付き） |
| `ExpandToNexusOperator` | Vertex → 所属Nexusの展開（型 / ロールフィルタ付き） |
| `ExpandMembersOperator` | Nexus → メンバーVertexの展開（ロールフィルタ、起点Vertex除外付き） |
| `CoMembershipOperator` | 設定済みロール対の co-membership 1-hop（下記の物理ビューを使用） |

結果行のNexus列は `TupleSlotType.NexusId` のスロットに載り、
`QueryRow.GetNexusId` で取り出せる。
未知のロール名や型名は例外ではなく空結果になる。

### co-membership の物理ビュー {#co-membership-view}

co-membership（起点Vertex → 所属Nexus → 別ロールのメンバー、という 1 論理ホップ）は
既定では incidence チェーンの 2 段展開で評価される。
`YatagarasuDatabaseOptions.CoMembershipRolePairs` にロール対（例: `subject` → `object`）を登録すると、
その対だけを物理化したメモリ内ブロックが open 時と vacuum 後に header / incidence から再構築され、
該当する走査がブロック読みに切り替わる。

- ビューは導出データであり、正本は header と incidence である。追加の永続ストレージは持たない
- 作成差分は durable commit 後に公開される。同一トランザクション内に未コミット差分がある間、
  および未設定のロール対は、リンクチェーン走査へフォールバックする
- 追加メモリと構築コストは、登録したロール対に一致するメンバー組数に比例する

### フィルタ / 集合 {#filter-ops}

| オペレータ | 説明 |
|---|---|
| `FilterOperator` | 述語ベースの行フィルタ |
| `BitmapFilterOperator` | ビットマップで高速化したフィルタ |
| `UnionOperator` | 2 つのオペレータ出力の集合和 |
| `CoalesceOperator` | 順序付きソースから最初の非空結果 |

### 集約 / 射影 {#agg-ops}

| オペレータ | 説明 |
|---|---|
| `ProjectOperator` | 列の射影 / 変換 |
| `SortOperator` | インメモリソート |
| `LimitOperator` | 行数制限 |

### 全文 / ベクトル {#fts-vec-ops}

| オペレータ | 説明 |
|---|---|
| `FullTextScanOperator` | BM25 スコア付き全文検索 |
| `FilteredFullTextScanOperator` | 述語フィルタ付き全文検索 |

両 operator は transaction snapshot から同じ immutable全文 manifest と corpus stats を解決する。

`FullTextScanOperator` は owner Generation と `PropertyVersionRef` を primary store で再検証し、除外後に次点を補充して top-k を確定する。

`FilteredFullTextScanOperator` は上流の full `VertexId` を primary `Read` で検証してから physical candidate setへ変換する。
| `KnnVertexSourceOperator` | K 近傍ベクトル検索 |
| `FilteredKnnVertexSourceOperator` | 述語フィルタ付き KNN |

KNN は read transaction の snapshot から vector definition と可視 manifest を解決する。
logical row と filtered candidate は full typed ID を保持し、raw sequence を transaction 入力へ渡さない。
segment candidate は primary property で owner generation、property visibility、target、payload checksum を再検証してから出力する。

## Traversal DSL {#traversal-dsl}

`GraphTraversalSource` (`Yatagarasu.Api`) は Gremlin 風の読み取り専用走査 API を提供する。
`IReadTransaction.Query` と `IWriteTransaction.Query` は、それぞれのトランザクションに束縛された source を返す。

```csharp
using var read = db.BeginReadTransaction();
var names = read.Query.Vertices()
    .HasLabel("Person")
    .Has("name", "Alice")
    .Out("KNOWS")
    .Values<string>("name")
    .ToList();
```

作成、更新、削除、merge は `IWriteTransaction.Mutate` の `GraphMutationSource` から開始する。
読み取り source に mutation メソッドは公開しない。

### Nexus の走査 {#nexus-dsl}

Nexusは無向でロール付きのため、方向動詞（`Out` / `In`）は使わない。
ロールフィルタが方向の一般化にあたる。

```csharp
// 作成: builder にロール付きメンバーとプロパティを積み、Next() で確定する
using var write = db.BeginWriteTransaction();
var factId = write.Mutate.AddNexus("Fact")
    .Member("subject", alice)
    .Member("object", yatagarasu)
    .Member("source", chunk)
    .P("status", "verified")
    .Next();
write.Commit();

// Vertex → Nexus → メンバーの走査
read.Query.Vertex(alice)
 .Nexuses("Fact", role: "subject")   // alice が subject として属す Fact
 .Members("object");                     // その Fact の object メンバー

// 起点Vertexを除いた co-membership
read.Query.Vertex(alice).Nexuses("Purchase", "buyer").OtherMembers("item");
```

### 有向NexusのAND到達と最短導出 {#directed-nexus-algorithms}

`DirectedNexusAlgorithms` は、Nexus型とtail/headロールを指定して、読み取りトランザクションの
同一snapshot上で有向Nexus網を探索する。`FindReachableVertices` は、あるNexusの全tailが
到達済みになったときだけ全headを到達済みにする。通常の二項Edge到達へ展開したときの
「tailのどれか1つで進める」という意味にはならない。

```csharp
var reachable = read.FindReachableVertices(
    seeds, "Reaction", "reactant", "product",
    new DirectedNexusReachabilityOptions { MaxResults = 100_000 });

var shortest = read.FindShortestDerivation(
    seeds, target, "Reaction", "reactant", "product",
    static (tx, nexus) => tx.GetProperty(nexus, "cost").DoubleValue,
    DerivationCostMode.Additive,
    new ShortestDerivationOptions { MaxTreeNodes = 100_000 });
```

最短導出は0以上の有限Nexusコストだけを受け付ける。`Additive` はNexusコストと全tailの
導出コストを加算し、`Bottleneck` はそれらの最大値を取る。返却する `Tree` はpreorderの
平坦な木で、各要素の `ParentIndex` が親を指す。共有された導出も木に現れるたびに別の出現として
保持し、加法コストでも出現ごとに数える。同コスト候補はVertex確定前なら小さいNexus IDを優先し、
確定済みVertexの導出は変更しない。対象Vertexをpriority queueから正しい最小コストで確定した時点で
導出木を復元し、対象Vertexから先の無関係なNexusは走査しない。

到達結果と最短導出結果は `TerminationReason` と `IsComplete` を持つ。`MaxResults`、
`MaxNexuses`、`MaxTreeNodes` で作業量を制限でき、キャンセル時は `OperationCanceledException` を送出する。
探索時間は触れたNexusのmember総数と優先度queue操作、作業領域は到達Vertex・触れたNexus・
返却する木の大きさに比例する。メンバー走査は `GetNexuses` / `GetMembers` を使うため、
未commitまたは読み取り開始後にcommitされたNexusは観測しない。

### 最小ヒッティング集合 {#minimum-hitting-set}

`MinimumHittingSetAlgorithms` は、集合族の各集合を少なくとも一つのVertexで被覆する
最小ヒッティング集合を専用の厳密solverで求める。`Solve` は明示的なVertex集合族を受け取り、
`FindMinimumHittingSet` は指定型の各Nexusを一つの集合、指定ロールのmemberを候補Vertexとして
同じ計算を行う。

```csharp
var result = read.FindMinimumHittingSet(
    "Requirement",
    "provider",
    new MinimumHittingSetOptions
    {
        MaxNodes = 100_000,
        TimeLimit = TimeSpan.FromSeconds(1),
    });
```

solverは候補と集合の包含関係を縮約し、greedyで実行可能解を作り、互いに素な集合packingと
最大被覆数から下界を求める。下界から上界までの基数について分枝限定のdecisionを実行し、
入力サイズだけで近似解へ切り替えない。

最初の実行可能証明書には全制約の確認が必要なため、solverは入力全体の正規化とfallback解の
構築を一度完了してから、`MaxNodes`、`TimeLimit`、`CancellationToken` を協調的に観測する。
これらの上限はこの初回passを途中でpreemptしない。Nexus adapterはsnapshot入力を全てmaterializeしてから
solverを呼ぶため、adapter走査はsolverのtime budgetに含まれず、cancellationでも途中終了しない。

実行可能な場合、結果の `Solution` は常に全集合を被覆し、その要素数が `UpperBound` になる。
`LowerBound` は証明済み下界であり、`IsOptimal` が真なら上下界は一致する。
node、time、cancellationの上限に達した場合も、この証明契約を保ったまま終了理由を返す。
空の集合族には空の最適解を返す。空集合を一つでも含む場合は `HasSolution=false`、
`Infeasible` とし、上下界は返さない。

Nexus adapterは全Nexus走査と `GetMembers` を読み取りトランザクション内で実行する。
未commitのNexusと読み取り開始後にcommitされたNexusは候補集合へ入らない。

## 組み込みグラフ注釈 {#graph-annotations}

`GraphAnnotationAlgorithms` は `IReadTransaction` のsnapshot可視な二項Edgeを入力にし、
既存queryとは独立した明示opt-in経路で次の固定注釈を評価する。

- `EvaluateReachability`: Boolean注釈を`BreadthFirst`で評価する。
- `EvaluateTropical`: 加算経路コストの最小値を、非負辺の`LabelSetting`、
  DAGの`AcyclicDynamicProgramming`、または`BoundedWorklist`で評価する。
- `EvaluateViterbi`: 0以上1以下のEdge確率の最大積を、DAGの`AcyclicDynamicProgramming`で評価する。
- `EvaluatePathMultiplicity`: 非負整数のwalk多重度を`BoundedWorklist`で評価する。

policyは呼び出しごとに明示し、値の性質から自動選択しない。
label-settingは負辺を、DAG専用policyはcycleを、ViterbiはNaN、Infinity、0未満、1超過を、
いずれもrelaxation前に拒否する。
Edge値selectorの例外は変換せず呼び出し元へ伝播する。

adapterは全Vertexをfull packed-ID順へ並べ、各Vertexのoutgoing EdgeをEdge ID順へ並べる。
結果もVertex ID順で決定的に返す。
`EdgeType`を指定した場合はその型だけを入力にする。
snapshot走査の開始から`MaxVertices`、`MaxEdges`、`MaxRelaxations`、`MaxAnnotationUpdates`、
`TimeLimit`、`CancellationToken`を観測する。
入力materializationの時間・空間はO(V+E)、BFSと各DAG DPの評価時間はO(V+E)、
label-settingはO((V+E) log V)である。
bounded worklistの評価時間は実際の緩和回数に比例し、公開budgetが上限になる。

`Completed`だけがsnapshot全体について`IsComplete=true`かつ`IsExact=true`である。
`MaxResults`は評価済み注釈の決定的prefixと`TotalAnnotationCount`を返すが、
返していない注釈があるため`MaxResultsReached`、`IsExact=false`になる。
入力走査、検証、評価を完了できなかった場合は、途中の値を正解と誤認させないため注釈を返さない。

```csharp
var costs = read.EvaluateTropical(
    source,
    GraphAnnotationPolicy.LabelSetting,
    (transaction, edge) => transaction.GetProperty(edge, "cost").DoubleValue,
    new GraphAnnotationOptions
    {
        EdgeType = "Route",
        MaxVertices = 10_000,
        MaxEdges = 100_000,
        MaxRelaxations = 200_000,
        TimeLimit = TimeSpan.FromSeconds(1),
    });
```

## 形式概念列挙 {#formal-concepts}

`EnumerateFormalConcepts` は指定型のNexusを属性、指定ロールのmember Vertexを対象、membershipを
incidenceとするformal contextから、Galois閉包の形式概念を列挙する。
対象universeは、指定ロールのincidenceに実際に現れるVertexだけであり、同じラベルの孤立Vertexや
別ロールだけに現れるVertexを暗黙に補わない。
extentとintentはkind、Generation、Sequenceを含むfull packed-ID順で返す。

ページ列挙はNextClosure、一括列挙はClose-by-Oneを利用できる。
`Auto` は継続が必要な形ではNextClosure、一括形ではClose-by-Oneを選び、両アルゴリズムの役割を分ける。
`MinExtent`と`MinIntent`は結果filterであり、指数的な候補空間そのものを有限にしない。
`MaxResults`、`MaxClosureEvaluations`、`MaxObjects`、`MaxAttributes`、`MaxIncidences`、
`TimeLimit`、`CancellationToken`を必ず有限実行契約として扱う。

```csharp
var options = new FormalConceptOptions
{
    Strategy = FormalConceptEnumerationStrategy.NextClosure,
    PageSize = 100,
    MaxResults = 10_000,
    MaxClosureEvaluations = 1_000_000,
};
var page = read.EnumerateFormalConcepts("Capability", "object", options);
```

continuationはcontext、対象順、属性順、filter、option、version、作成元transaction objectとIDを束縛する。
再開時はNexus incidenceを再materializeしてclosure評価前に照合するため、同じwrite transaction内の変更も拒否する。
公開stable snapshot IDは存在しないため、reopenや別transactionを跨ぐ継続はサポートしない。
`TimeLimit`は値を束縛した呼び出しごとの時間枠、`CancellationToken`はfingerprintに含めない呼び出しごとの停止要求である。
キャンセル後は同じpositionを新しいcancellation tokenで再開でき、closure作業量はcontinuationを通じて累積する。
終了理由と`IsComplete`、`IsTruncated`を確認し、打ち切りprefixを全概念と解釈してはならない。

### merge ルックアップ {#merge-lookup}

`MergeEdge` は source、target、Edge 型の組を専用ルックアップで検索する。
`MergeNexus` は Nexus 型と、ロールとメンバーの正規化済み集合を専用ルックアップで検索する。
Nexus の入力順序は同一性に影響しない。
両ルックアップは open 時に primary store から再構築できる導出 view であり、候補の MVCC 可視性と generation を primary record で再検証する。
rollback、削除、slot 再利用で stale になった候補は採用しない。

- `Nexuses(type?, role?)` はVertex起点でNexus ID の走査を返す。
  `g.Nexuses()` は全スキャン起点、`g.Nexus(id)` は単一起点
- `Members(role?)` はメンバーVertexへ展開する。同じロールに複数Vertexが属す場合は
  Vertexごとに 1 行を返す
- `OtherMembers(role?)` はVertex起点の `Nexuses` から続けたときだけ使え、
  起点Vertex自身を全ロールから除外する。起点情報のない走査（全スキャン起点など）から呼ぶと
  例外になる
- `As(alias)` で束縛したNexus列へは `Select<NexusId>(alias)` で型検査付きで戻れる。
  Vertexを束縛した alias を `Select<NexusId>` で参照すると例外になる。
  これにより「fact の object と source を 1 つのオペレータツリーで取る」形の
  n 項クエリを途中 materialize なしで合成できる

### Nexus の型付き走査 {#nexus-typed-dsl}

Source Generator は `[Nexus]` クラスのロールプロパティごとに型保存の糖衣
（`{クラス名}As{プロパティ名}` / `{プロパティ名}` / `Other{プロパティ名}`）を生成する。
実行は型なしと同じ論理オペレータへ委譲され、別経路を持たない。

```csharp
g.Vertices<Person>().Has(p => p.Name, "Alice")
 .EmploymentAsEmployee()                 // Person → Employment (ロール Employee)
 .Has(e => e.Title, "Engineer")          // Nexusプロパティで絞る
 .Employer()                              // ロール Employer のメンバーへ (TypedGraphTraversal<Company>)
 .ToList();
```

## Match パターン {#match}

`Match` は Cypher 風のパターン式を物理オペレータツリーへコンパイルする。パターンはVertexラベル、
Edge型、プロパティ述語を指定し、それらはインデックスシークと expand 操作へ最適化される。

### Nexus の星型パターン {#nexus-match}

線形の vertex-edge-vertex パターンとは別に、1 つのNexusと複数のロール付きメンバーを
同じ結果行へ束縛する星型パターンがある。

```csharp
var rows = g.Match(
    GraphPattern.Nexus("f", "Fact")
        .Member("subject", GraphPattern.Vertex("s", "Entity"))
        .Member("object",  GraphPattern.Vertex("o", "Entity"))
        .Member("source",  GraphPattern.Vertex("src", "Chunk")))
 .Where("f", "status", P.Eq("verified"))
 .Return(ctx => (Fact: ctx.Nexus("f"), Subject: ctx.Vertex("s"), Object: ctx.Vertex("o")))
 .ToList();
```

- 最初のメンバーが anchor になり、星型に展開される
- プロパティ述語は変数の種類（Vertex / Nexus）に応じたエンティティで評価される
- 同じロールに複数メンバーが属す場合、束縛の組み合わせごとに 1 行を返す
- 結果行からは `ctx.Nexus(alias)` / `ctx.NexusGet<T>(alias, key)` で
  Nexus ID とそのプロパティを取り出せる

### 高密度三角形結合の内部経路 {#cyclic-triangle-join}

クエリエンジン内部には、三本の有向二項relation
`R(a,b) ∧ S(b,c) ∧ T(c,a)`だけを対象とする三角形結合経路がある。
各relationは既存の1-edge `Match`から抽出し、query-localなソート済み列を構築して
`S(b,*)`と`T(*,a)`をintersectionする。
永続索引、cache、ストレージ形式は追加しない。

optimizerは、三relationのpairが重複せず、`R⋈S`の推定中間行が32,768以上かつ
入力三relationの合計行数の4倍以上になる場合だけtransient column経路を選ぶ。
小規模、低増幅、pair重複を含む入力はmaterializing経路へ戻す。
重複時のfallbackはbag semanticsを維持し、同じtupleをrelationごとの重複度の積だけ返す。
空relationは作業領域を構築せず空結果になる。

結果はGenerationを含む`VertexId`の辞書順で決定的に返す。
この経路は公開Match grammarを拡張せず、既存の1-edge Match、Nexus star、acyclic pathの
plannerと物理operatorを置換しない。
