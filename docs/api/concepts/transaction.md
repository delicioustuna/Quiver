# Transaction

Yatagarasu の書き込みはすべてトランザクション境界の中で行う。`YatagarasuDatabase.BeginWriteTransaction()` で新規トランザクションを開始し、`Commit()` または `Rollback()` で終了する。

## 基本パターン

```csharp
using var db = YatagarasuDatabase.Open("./mygraph");
using (var tx = db.BeginWriteTransaction())
{
    var n = tx.CreateVertex("Person");
    tx.SetProperty(n, "name", PropertyValue.FromString("Alice"));
    tx.Commit();
}
```

## 読み取りと書き込みの能力

Yatagarasu はスナップショット分離を提供する。
`BeginReadTransaction()` は `IReadTransaction` を返し、読み取りと `Query` だけを公開する。
`BeginWriteTransaction()` は `IWriteTransaction` を返し、読み取り能力に加えて mutation、`Mutate`、`EditSchema`、commit、rollback を公開する。
並列トラバーサル系オペレータは読み取り専用トランザクションでのみ実行可能である。

## コミットフック

`IWriteTransaction.OnCommitted` / `OnRolledBack` でコミット後/ロールバック後のコールバックを登録できる。
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
    loader.AppendVertex(new VertexId(i), labelId);
}
loader.Commit();
```

10 億エッジ級では `BeginStreamingBulkLoad()` を使うと、Edgeを一時ファイルにストリーミングしてピークヒープを抑えられる。
