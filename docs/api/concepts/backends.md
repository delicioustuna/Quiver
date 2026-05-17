# Backends

Quiver は `IGraphStorageBackend` 抽象を介してストレージレイアウトを切り替えられる。

## 組み込みバックエンド

| バックエンド | 状態 | 用途 |
|---|---|---|
| **Binary** | ✅ 既定 | 独自バイナリページフォーマット。高性能・本番向け |
| **SQLite** | ✅ MVP | SQLite を裏に持つ。エコシステム連携・運用ツール充実 |

## 選択

```csharp
using var db = GraphDatabase.Open(
    "./mygraph",
    new GraphDatabaseOptions { Backend = BackendKind.Binary });
```

## カスタムバックエンドの注入 (テスト向け)

```csharp
using var db = GraphDatabase.Open(
    "./mygraph",
    new GraphDatabaseOptions
    {
        BackendFactory = new MyInMemoryBackendFactory(),
    });
```

## バックエンドのケイパビリティ

- `IGraphStorageBackend.Vectors` — ベクトルストア (バイナリ MVP / SQLite MVP では in-memory)
- `IGraphStorageBackend.Access` — `IGraphAccessMethods` 抽象を経由した access path (BA-3)
- `IGraphStorageBackend.BulkLoad` — `BulkLoadCapabilities` で利用可能なバルクロード経路を表す
