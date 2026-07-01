# Transaction

Quiver の書き込みはすべてトランザクション境界の中で行う。
同期コードでは `GraphDatabase.BeginTransaction()` で開始し、`Commit()` または `Rollback()` で終了する。
非同期コードでは `BeginTransactionAsync()` と `CommitAsync()` を境界として使用できる。

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

## 非同期パターン

`GraphDatabase` と `IGraphTransaction` は `IAsyncDisposable` に対応する。
`CommitAsync()` は WAL の fsync 完了を非同期に待ち、完了後は同期版の `Commit()` と同じ永続性を保証する。

```csharp
await using var db = GraphDatabase.Open("./mygraph");
await using var tx = await db.BeginTransactionAsync();

var n = tx.CreateNode("Person");
tx.SetProperty(n, "name", PropertyValue.FromString("Alice"));

await tx.CommitAsync();
```

トランザクション内部の CRUD と traversal は同期処理である。
`BeginTransactionAsync()` から `CommitAsync()` までの間に、ネットワーク呼び出しや UI 待機等の任意の `await` を挟んではならない。
許可される await は、Quiver が提供する開始、commit、破棄、結果取得の境界である。

完全な例は [`Quiver.Samples.AsyncApi`](../../../samples/Quiver.Samples.AsyncApi/) を参照。

## 分離レベル

既定はスナップショット分離 (`IsolationLevel.SnapshotIsolation`)。
読み取り専用トランザクションは `BeginReadOnlyTransaction()` または `BeginReadOnlyTransactionAsync()` で開始する。
並列トラバーサル系オペレータ (`ParallelBfsOperator` など) は読み取り専用トランザクションでのみ実行可能。

## コミットフック

`IGraphTransaction.OnCommitted` / `OnRolledBack` でコミット後/ロールバック後のコールバックを登録できる。
コミットフックはストレージへの永続化が完了した後に実行される。

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
