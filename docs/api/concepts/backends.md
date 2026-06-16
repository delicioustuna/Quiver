# Backends

Quiver は `IGraphStorageBackend` 抽象を介してストレージレイアウトを切り替えられる。

## 組み込みバックエンド

| バックエンド | 状態 | 用途 |
|---|---|---|
| **Binary** | ✅ 既定 | 独自バイナリページフォーマット。高性能・本番向け |

## 選択

```csharp
using var db = GraphDatabase.Open("./mygraph.quiver");
```

## カスタムバックエンドの注入 (テスト向け)

```csharp
using var db = GraphDatabase.Open(
    "./mygraph.quiver",
    new GraphDatabaseOptions
    {
        BackendFactory = new MyInMemoryBackendFactory(),
    });
```

## バックエンドのケイパビリティ

- `IGraphStorageBackend.Vectors` — ベクトルストア (binary backend は永続化対応)
- `IGraphStorageBackend.Access` — `IGraphAccessMethods` 抽象を経由した access path (BA-3)
- `IGraphStorageBackend.BulkLoad` — `BulkLoadCapabilities` で利用可能なバルクロード経路を表す
