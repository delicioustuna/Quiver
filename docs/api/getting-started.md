# Getting Started

## 必要環境

- .NET 10 SDK 以上
- C# 13 以上

## インストール

`Yatagarasu`パッケージには、コアエンジン、モデル属性、Source Generatorが含まれます。

```bash
dotnet add package Yatagarasu --version 0.7.0
```

## はじめてのグラフ

`GraphStore`は通常操作を同期callbackへ閉じ込めます。write callbackが正常終了すればcommitし、例外ならrollbackします。

```csharp
using Yatagarasu;

using var store = GraphStore.Open("./mygraph.yata");

(VertexKey Alice, VertexKey Bob) people = store.Write(write =>
{
    VertexKey alice = write.CreateVertex("Person");
    VertexKey bob = write.CreateVertex("Person");
    write.Set(alice, "name", "Alice");
    write.Set(bob, "name", "Bob");
    write.Connect(alice, "KNOWS", bob);
    return (alice, bob);
});

IReadOnlyList<VertexKey> known = store.Read(read =>
    read.Query.Vertices(people.Alice).Out("KNOWS").ToList());
```

`VertexKey`、`EdgeKey`、`NexusKey`と`GraphValue`はcallbackの外へ安全に保持できます。callbackのscope自体は終了後に失効します。

長時間snapshotや明示commitが必要な処理は`store.Advanced.BeginRead()` / `BeginWrite()`を使います。Source Generatorの型付きmapperは`GraphWorkspace`から利用できます。

## 次のステップ

- [API surface snapshot](https://github.com/delicioustuna/Yatagarasu/blob/main/tests/Yatagarasu.PublicApi.Tests/PublicApi/Yatagarasu.approved.txt) — 承認済みの公開API一覧
- [as-built仕様](https://github.com/delicioustuna/Yatagarasu/blob/main/docs/spec/00_overview.md) — ストレージと実行契約
- [開発ガイド](https://github.com/delicioustuna/Yatagarasu/blob/main/docs/design/development.md) — アーキテクチャ、ビルド、テスト
