# クエリエンジン

> as-built 仕様 (on-disk FormatVersion V2)

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

## Match パターン {#match}

`Match` は Cypher 風のパターン式を物理オペレータツリーへコンパイルする。パターンはノードラベル、
リレーションシップ型、プロパティ述語を指定し、それらはインデックスシークと expand 操作へ最適化される。
