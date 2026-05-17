# MERGE / UPSERT

Cypher の `MERGE (n:Label {key: value})` 相当の操作を `IGraphTransaction.MergeNode` で提供する。

```csharp
var (id, created) = tx.MergeNode(
    label: "Person",
    matchKey: "email",
    matchValue: PropertyValue.FromString("alice@example.com"));

if (created)
    tx.SetProperty(id, "createdAt", PropertyValue.FromInt64(now));
else
    tx.SetProperty(id, "lastSeenAt", PropertyValue.FromInt64(now));
```

## マッチセマンティクス

- ラベルが一致し、`matchKey` の値が `matchValue` と等しい既存ノードを探す
- 重複が複数ある場合、`NodeId` 順で最初にヒットしたものを採用
- 該当が無ければ新規ノードを確保し、`matchKey: matchValue` をセットして返す
- `Created` は新規作成だったかどうかを示す

## 等値判定の規則

| 型 | 判定方式 |
|---|---|
| `String` / `Bytes` | バイト単位の完全一致 |
| `Double` | ビット完全一致 |
| `Bool` / `Int32` / `Int64` | スカラ等値 (符号拡張あり) |

## GC-5: トラバーサルソース経由の糖衣構文

```csharp
var (id, created) = g.MergeNode("Person", "email", PropertyValue.FromString("alice@example.com"));
```
