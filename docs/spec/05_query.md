# クエリエンジン

> as-built 仕様 (on-disk FormatVersion V5)
>
> **current (as-built)**: 以下は現在実装されている FormatVersion V5 のクエリ契約である。
> **target (未実装)**: [Single Writer + Snapshot Readers 抜本再設計](../../plans/single-writer-redesign.md) が将来の設計正本であり、本書の本文はその target を先取りして記述しない。
> **実装済み境界**: 再設計の production code はまだ実装されていない。`redesign-baseline` は着工前の測定を固定するタグであり、再設計の実装完了を表さない。

## アーキテクチャ {#architecture}

クエリエンジンは **Volcano / iterator** モデルに従う。論理プランは最適化され、物理オペレータツリーへ
コンパイルされる。各オペレータは `GetNext()` を実装し、次の結果行を pull する。

## GraphKernel {#graph-kernel}

`GraphKernel` (`src/Quiver/Operators/GraphKernel.cs`) は、コアとなるグラフ走査の抽象を提供する。
`IGraphAccessMethods.Expand()` をラップすることで、異なるストレージアクセスパスをまたいで
同一のアルゴリズム本体が動作する。

### Kernel コントラクト {#kernel-contract}

```csharp
interface IGraphKernel<TState>
{
    void Initialize(NodeId source, ref TState state);
    bool VisitNeighbor(NodeId source, NodeId target, RelationshipId relId,
                       double weightRaw, int depth, ref TState state);
    bool ShouldContinue(int depth, TState state);
}
```

## 物理オペレータ {#physical-operators}

### スキャンオペレータ {#scan-ops}

| オペレータ | 説明 |
|---|---|
| `AllNodesScanOperator` | 全ライブノードのシーケンシャルスキャン |
| `NodeByLabelScanOperator` | スキャン中にラベルでフィルタ |
| `AllRelationshipsScanOperator` | 全リレーションシップのシーケンシャルスキャン |

### Expand / 走査 {#expand-ops}

| オペレータ | 説明 |
|---|---|
| `ExpandOperator` | 単一ホップ展開 |
| `VariableLengthExpandOperator` | min/max depth 付きのマルチホップ |
| `BfsOperator` | 幅優先走査 |
| `ShortestPathOperator` | 重みなし最短経路 (BFS) |
| `WeightedShortestPathOperator` | Dijkstra ベースの重み付き最短経路 |
| `BidirectionalExpandOperator` | 経路探索のための双方向 BFS |
| `RelationshipScanExpandOperator` | リレーションシップスキャンによる展開 |
| `RelationshipEndpointOperator` | リレーションシップのエンドポイントを解決 |

### Hyperedge {#hyperedge-ops}

| オペレータ | 説明 |
|---|---|
| `AllHyperedgesScanOperator` | 全ライブハイパーエッジのシーケンシャルスキャン（型フィルタ付き） |
| `ExpandToHyperedgeOperator` | ノード → 所属ハイパーエッジの展開（型 / ロールフィルタ付き） |
| `ExpandMembersOperator` | ハイパーエッジ → メンバーノードの展開（ロールフィルタ、起点ノード除外付き） |
| `CoMembershipOperator` | 設定済みロール対の co-membership 1-hop（下記の物理ビューを使用） |

結果行のハイパーエッジ列は `TupleSlotType.HyperedgeId` のスロットに載り、
`QueryRow.GetHyperedgeId` で取り出せる。
未知のロール名や型名は例外ではなく空結果になる。

### co-membership の物理ビュー {#co-membership-view}

co-membership（起点ノード → 所属ハイパーエッジ → 別ロールのメンバー、という 1 論理ホップ）は
既定では incidence チェーンの 2 段展開で評価される。
`GraphDatabaseOptions.CoMembershipRolePairs` にロール対（例: `subject` → `object`）を登録すると、
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
| `KnnNodeSourceOperator` | K 近傍ベクトル検索 |
| `FilteredKnnNodeSourceOperator` | 述語フィルタ付き KNN |

## Traversal DSL {#traversal-dsl}

`GraphTraversalSource` (`Quiver.Api`) は Gremlin 風の流暢な走査 API を提供する:

```csharp
var g = tx.G(schema);
g.V("Person").Has("name", "Alice")
 .Out("KNOWS")
 .Values<string>("name");
```

### Hyperedge の走査 {#hyperedge-dsl}

ハイパーエッジは無向でロール付きのため、方向動詞（`Out` / `In`）は使わない。
ロールフィルタが方向の一般化にあたる。

```csharp
// 作成: builder にロール付きメンバーとプロパティを積み、Next() で確定する
var factId = g.AddHyperedge("Fact")
    .Member("subject", alice)
    .Member("object", quiver)
    .Member("source", chunk)
    .P("status", "verified")
    .Next();

// ノード → ハイパーエッジ → メンバーの走査
g.Node(alice)
 .Hyperedges("Fact", role: "subject")   // alice が subject として属す Fact
 .Members("object");                     // その Fact の object メンバー

// 起点ノードを除いた co-membership
g.Node(alice).Hyperedges("Purchase", "buyer").OtherMembers("item");
```

- `Hyperedges(type?, role?)` はノード起点でハイパーエッジ ID の走査を返す。
  `g.Hyperedges()` は全スキャン起点、`g.Hyperedge(id)` は単一起点
- `Members(role?)` はメンバーノードへ展開する。同じロールに複数ノードが属す場合は
  ノードごとに 1 行を返す
- `OtherMembers(role?)` はノード起点の `Hyperedges` から続けたときだけ使え、
  起点ノード自身を全ロールから除外する。起点情報のない走査（全スキャン起点など）から呼ぶと
  例外になる
- `As(alias)` で束縛したハイパーエッジ列へは `Select<HyperedgeId>(alias)` で型検査付きで戻れる。
  ノードを束縛した alias を `Select<HyperedgeId>` で参照すると例外になる。
  これにより「fact の object と source を 1 つのオペレータツリーで取る」形の
  n 項クエリを途中 materialize なしで合成できる

### Hyperedge の型付き走査 {#hyperedge-typed-dsl}

Source Generator は `[Hyperedge]` クラスのロールプロパティごとに型保存の糖衣
（`{クラス名}As{プロパティ名}` / `{プロパティ名}` / `Other{プロパティ名}`）を生成する。
実行は型なしと同じ論理オペレータへ委譲され、別経路を持たない。

```csharp
g.Nodes<Person>().Has(p => p.Name, "Alice")
 .EmploymentAsEmployee()                 // Person → Employment (ロール Employee)
 .Has(e => e.Title, "Engineer")          // ハイパーエッジプロパティで絞る
 .Employer()                              // ロール Employer のメンバーへ (TypedGraphTraversal<Company>)
 .ToList();
```

## Match パターン {#match}

`Match` は Cypher 風のパターン式を物理オペレータツリーへコンパイルする。パターンはノードラベル、
リレーションシップ型、プロパティ述語を指定し、それらはインデックスシークと expand 操作へ最適化される。

### Hyperedge の星型パターン {#hyperedge-match}

線形の node-edge-node パターンとは別に、1 つのハイパーエッジと複数のロール付きメンバーを
同じ結果行へ束縛する星型パターンがある。

```csharp
var rows = g.Match(
    GraphPattern.Hyperedge("f", "Fact")
        .Member("subject", GraphPattern.Node("s", "Entity"))
        .Member("object",  GraphPattern.Node("o", "Entity"))
        .Member("source",  GraphPattern.Node("src", "Chunk")))
 .Where("f", "status", P.Eq("verified"))
 .Return(ctx => (Fact: ctx.Hyperedge("f"), Subject: ctx.Node("s"), Object: ctx.Node("o")))
 .ToList();
```

- 最初のメンバーが anchor になり、星型に展開される
- プロパティ述語は変数の種類（ノード / ハイパーエッジ）に応じたエンティティで評価される
- 同じロールに複数メンバーが属す場合、束縛の組み合わせごとに 1 行を返す
- 結果行からは `ctx.Hyperedge(alias)` / `ctx.HyperedgeGet<T>(alias, key)` で
  ハイパーエッジ ID とそのプロパティを取り出せる
