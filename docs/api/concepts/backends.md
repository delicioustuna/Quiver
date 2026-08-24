# Backends

Yatagarasu は `IGraphStorageBackend` 抽象を介してストレージレイアウトを切り替えられる。

## 組み込みバックエンド

| バックエンド | 状態 | 用途 |
|---|---|---|
| **Binary** | ✅ 既定 | 独自バイナリページフォーマット。高性能・本番向け |

## 選択

```csharp
using var db = YatagarasuDatabase.Open("./mygraph.yata");
```

## カスタムバックエンドの注入 (テスト向け)

```csharp
using var db = YatagarasuDatabase.Open(
    "./mygraph.yata",
    new YatagarasuDatabaseOptions
    {
        BackendFactory = new MyInMemoryBackendFactory(),
    });
```

## バックエンドのケイパビリティ

- transaction-scoped KNN と vector property は、binary と in-memory の両 backend が同じ契約で提供する
- `IGraphStorageBackend.Access` — `IGraphAccessMethods` 抽象を経由した access path
- `IGraphStorageBackend.BulkLoad` — `BulkLoadCapabilities` で利用可能なバルクロード経路を表す
