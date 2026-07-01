# Getting Started

## 必要環境

- .NET 10 SDK 以上
- C# 13 以上

## インストール

現状 Quiver は NuGet パッケージ化されていません。リポジトリをクローンして直接プロジェクト参照する想定です。

```bash
git clone <quiver-repo-url>
cd Quiver
dotnet build Quiver.slnx
```

## はじめてのグラフ

```csharp
using Quiver;
using Quiver.Storage.Records;

using var db = GraphDatabase.Open("./mygraph");
using var tx = db.BeginTransaction();

var alice = tx.CreateNode("Person");
var bob   = tx.CreateNode("Person");
tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
tx.SetProperty(bob,   "name", PropertyValue.FromString("Bob"));
tx.CreateRelationship(alice, bob, "KNOWS");

tx.Commit();
```

非同期ホストでは、同期版を置き換えず、トランザクションの開始とコミットだけを非同期境界にできる。

```csharp
await using var db = GraphDatabase.Open("./mygraph");
await using var tx = await db.BeginTransactionAsync();

var alice = tx.CreateNode("Person");
tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));

await tx.CommitAsync();
```

トランザクション内部の CRUD は同期処理であり、開始からコミットまでの間に外部 I/O 等の任意の `await` を挟んではならない。
詳細は [Transaction](concepts/transaction.md) と [`Quiver.Samples.AsyncApi`](../../samples/Quiver.Samples.AsyncApi/) を参照。

## 次のステップ

- [Concepts](concepts/index.md) — モデル、トランザクション、トラバーサルの概念
- [Tutorials](tutorials/index.md) — 段階的に動かして学ぶ
- [API Reference](../api/Quiver.html) — 全公開 API リファレンス
