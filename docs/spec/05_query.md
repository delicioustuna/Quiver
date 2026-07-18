# クエリエンジン

> as-built 仕様（QUIVER-SW family version 2、2026-07-18）

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
`QuiverDatabaseOptions.CoMembershipRolePairs` にロール対（例: `subject` → `object`）を登録すると、
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
| `KnnVertexSourceOperator` | K 近傍ベクトル検索 |
| `FilteredKnnVertexSourceOperator` | 述語フィルタ付き KNN |

## Traversal DSL {#traversal-dsl}

`GraphTraversalSource` (`Quiver.Api`) は Gremlin 風の読み取り専用走査 API を提供する。
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
    .Member("object", quiver)
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
