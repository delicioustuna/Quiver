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
| `ShortestPathTo(target)` | 最短経路 |
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

## ストリーミング

`AsCursor()` / `AsEnumerable()` で大量結果をメモリを抑えて逐次処理できる。

```csharp
using var cursor = g.Nodes<Person>().AsCursor();
while (cursor.MoveNext())
{
    Process(cursor.Current);   // トランザクション有効期間内のみ有効
}
```
