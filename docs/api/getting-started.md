# Getting Started

## 必要環境

- .NET 10 SDK 以上
- C# 13 以上

## インストール

`Quiver`パッケージには、コアエンジン、モデル属性、Source Generatorが含まれます。

```bash
dotnet add package Quiver --version 0.2.0
```

## はじめてのグラフ

```csharp
using Quiver;
using Quiver.Storage.Records;

using var db = QuiverDatabase.Open("./mygraph");
using var tx = db.BeginWriteTransaction();

var alice = tx.CreateVertex("Person");
var bob   = tx.CreateVertex("Person");
tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));
tx.SetProperty(bob,   "name", PropertyValue.FromString("Bob"));
tx.CreateEdge(alice, bob, "KNOWS");

tx.Commit();
```

## 次のステップ

- [Concepts](concepts/index.md) — モデル、トランザクション、トラバーサルの概念
- [Tutorials](tutorials/index.md) — 段階的に動かして学ぶ
- [API surface snapshot](https://github.com/delicioustuna/Quiver/blob/main/tests/Quiver.PublicApi.Tests/PublicApi/Quiver.approved.txt) — 承認済みの公開 API 一覧
