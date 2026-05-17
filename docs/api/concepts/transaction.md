# Transaction

Quiver の書き込みはすべてトランザクション境界の中で行う。`GraphDatabase.BeginTransaction()` で新規トランザクションを開始し、`Commit()` または `Rollback()` で終了する。

## 基本パターン

```csharp
using var db = GraphDatabase.Open("./mygraph");
using (var tx = db.BeginTransaction())
{
    var n = tx.CreateNode("Person");
    tx.SetProperty(n, "name", PropertyValue.FromString("Alice"));
    tx.Commit();
}
```

## 分離レベル

既定はスナップショット分離 (`IsolationLevel.SnapshotIsolation`)。読み取り専用トランザクションは `BeginReadOnlyTransaction()` で開始する。並列トラバーサル系オペレータ (`ParallelBfsOperator` など) は読み取り専用トランザクションでのみ実行可能。

## コミットフック (VEC-3)

`IGraphTransaction.OnCommitted` / `OnRolledBack` でコミット後・ロールバック後のコールバックを登録できる。コミットフックはストレージへの永続化が完了した後に実行される。

```csharp
tx.OnCommitted(() => Console.WriteLine("永続化済み"));
tx.OnRolledBack(() => Console.WriteLine("破棄済み"));
```

## バルクロード

大量データの初期インポート向けには `BeginBulkLoad()` を使う。WAL を経由せず、レコード単位のメタフラッシュも省略するため、通常 TX に比べて 5 倍以上のスループットが期待できる。

```csharp
using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
for (long i = 0; i < 1_000_000; i++)
{
    loader.AppendNode(new NodeId(i), labelId);
}
loader.Commit();
```

10 億エッジ級では `BeginStreamingBulkLoad()` を使うと、リレーションシップを一時ファイルにストリーミングしてピークヒープを抑えられる。
