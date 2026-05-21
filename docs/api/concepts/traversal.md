# Traversal

`GraphTraversalSource` (`tx.G(schema)`) を起点とする Gremlin 風のトラバーサル DSL。チェーンステップで論理プランを組み立て、終端ステップ (`.ToList()` / `.Next()` / `.AsCursor()` 等) で物理プランに変換して実行する。

## 基本ステップ

| ステップ | 用途 |
|---|---|
| `Nodes()` / `Node(id)` | スキャン起点 |
| `HasLabel(label)` | ラベルでフィルタ |
| `Has(key, value)` / `Has(key, P.Gt(n))` | プロパティ等値・述語フィルタ |
| `Out()` / `In()` / `Both()` | 隣接ノードへ進む |
| `OutRelationships()` / `InRelationships()` | 隣接リレーションシップ自体を放出 |
| `SourceNode()` / `TargetNode()` / `OtherNode()` | エッジ端点ノードへ解決 |
| `Values(key)` | プロパティ値を取り出す |
| `Limit(n)` / `Skip(n)` / `Range(a, b)` | ページング |
| `OrderBy(key)` / `OrderByDescending(key)` | 並び替え |
| `Sum / Max / Min / Mean / GroupCount` | 集約 |
| `Repeat(s, n)` | 可変長トラバーサル |
| `ShortestPathTo(target)` | 最短経路 (ホップ数) |
| `WeightedShortestPath(s, t, weightKey)` | 重み付き最短経路 (Dijkstra / A*) |
| `Union / Coalesce / Optional` | 分岐合成 |
| `Where(t => …)` / `Not(t => …)` | サブトラバーサル述語 (WHERE EXISTS / NOT EXISTS) |
| `As(name)` / `Select(name)` / `Select(t => …)` | エイリアス pin と射影 |

## サブトラバーサル述語

```csharp
// KNOWS エッジを持たないノードのみ
var loners = g.Nodes().HasLabel("Person")
              .Not(t => t.Out("KNOWS"))
              .ToList();

// KNOWS 先に Person ラベルが少なくとも 1 件あるノードのみ
var connectors = g.Nodes().HasLabel("Person")
                  .Where(t => t.Out("KNOWS").HasLabel("Person"))
                  .ToList();
```

## 重み付き最短経路 (Dijkstra / A*)

`ShortestPathTo` がホップ数最短なのに対し、`WeightedShortestPath` はエッジの数値プロパティを
重みとして読み、重み合計が最小の経路を返す。結果 `WeightedPathResult` は距離・ノード列・
エッジ列をまとめて保持する (Volcano タプルに載らない可変長経路はタプルストリーム外で返す)。

```csharp
// Dijkstra — weight プロパティをエッジ重みとして読む
var path = g.WeightedShortestPath(src, dst, weightKey: "weight", type: "ROAD");
if (path.Found)
    Console.WriteLine($"距離 {path.Distance}, 経路 {string.Join("→", path.Nodes)}");

// A* — ノードの座標プロパティ (x/y or 緯度/経度) からヒューリスティックを自動生成
var astar = g.WeightedShortestPathAStar(src, dst, "weight", "x", "y", HeuristicMetric.Euclidean);

// A* — 推定残コストを直接渡す
var custom = g.WeightedShortestPath(src, dst, "weight", heuristic: node => EstimateRemaining(node));
```

- エッジ重みは**非負**でなければならない (負の重みを検出すると `InvalidOperationException`)。
- A* のヒューリスティックは consistent (単調) かつ非負であれば、Dijkstra と同じ最適解を
  より少ないノード展開で得られる。`HeuristicMetric.Euclidean` / `Haversine` は consistent。
- 重みプロパティを持たないエッジは重み `1.0` として扱う。

## ストリーミング

`AsCursor()` / `AsEnumerable()` で大量結果をメモリを抑えて逐次処理できる。

```csharp
using var cursor = g.Nodes<Person>().AsCursor();
while (cursor.MoveNext())
{
    Process(cursor.Current);   // トランザクション有効期間内のみ有効
}
```
