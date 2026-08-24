# MERGE / UPSERT

Cypher の `MERGE (n:Label {key: value})` 相当の操作を `IWriteTransaction.MergeVertex` で提供する。

```csharp
var (id, created) = tx.MergeVertex(
    label: "Person",
    matchKey: "email",
    matchValue: PropertyValue.FromString("alice@example.com"));

if (created)
    tx.SetProperty(id, "createdAt", PropertyValue.FromInt64(now));
else
    tx.SetProperty(id, "lastSeenAt", PropertyValue.FromInt64(now));
```

## マッチセマンティクス

- ラベルが一致し、`matchKey` の値が `matchValue` と等しい既存Vertexを探す
- 重複が複数ある場合、`VertexId` 順で最初にヒットしたものを採用
- 該当が無ければ新規Vertexを確保し、`matchKey: matchValue` をセットして返す
- `Created` は新規作成だったかどうかを示す

## パフォーマンス: インデックスを事前に作成すること

`MergeVertex` は **`(label, matchKey)` にインデックスが登録されていればそれを自動で利用し、無ければラベル内の全Vertexをスキャンする**。後者は O(N) で、ラベル内Vertex数が増えると秒オーダーになり得る (VertexCount=10000 のとき ~11 秒)。

業務キーで MERGE を多用する場合は、データベース起動直後に一度だけインデックスを作成する:

```csharp
using var db = YatagarasuDatabase.Open("./mygraph");
using (var schemaTx = db.BeginWriteTransaction())
{
    schemaTx.EditSchema.CreateIndex(new ScalarIndexDefinition(
        "idx_person_email",
        new PropertyTarget(PropertyOwnerKind.Vertex, "email", "Person"),
        IndexKind.StringEquality));
    schemaTx.Commit();
}

// 以降の MergeVertex("Person", "email", ...) は O(log n) シーク経路で実行される。
// MergeVertex が新規作成した場合のインデックスエントリ追加も自動で行われる。
```

インデックス未登録のキーで `MergeVertex` を呼ぶと、最初の 1 回だけ `System.Diagnostics.Trace.TraceWarning` で警告が出力される (フルスキャン経路に落ちたことを示すサイレント劣化の検出用)。インデックス名・ラベル・プロパティキーの対応は `db.Schema.ListIndexes()` から確認できる。

## 等値判定の規則

| 型 | 判定方式 |
|---|---|
| `String` / `Bytes` | バイト単位の完全一致 |
| `Double` | ビット完全一致 |
| `Bool` / `Int32` / `Int64` | スカラ等値 (符号拡張あり) |

## mutation source 経由の糖衣構文

```csharp
var (id, created) = tx.Mutate.MergeVertex(
    "Person",
    "email",
    PropertyValue.FromString("alice@example.com"));
```
