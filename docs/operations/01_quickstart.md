# 01. クイックスタート (運用者向け)

> **いつ読むか** — 初めて Quiver をアプリに埋め込むとき。この 1 ページだけで「DB を作る →
> スキーマ/索引を用意する → 最初の書き込みを commit する → 再起動後に読み戻す」までを通す。
> API レシピの網羅は [docs/cookbook.md](../cookbook.md)、概念解説は [docs/api/](../api/) を参照。

Quiver は **単一プロセスに埋め込む組み込みグラフ DB** であり、
サーバープロセスもネットワークポートも持たない。アプリと同じプロセス内で DB ディレクトリを
1 つ開いて使う。制約の全体像は [05_known_limits.md](05_known_limits.md) を参照。

---

## 1. 最小構成 — 直接 API で開く

NuGet では `Quiver` パッケージ 1 つに依存すればよい。DB は「ディレクトリ」単位で、
存在しなければ初回 `Open` 時に作成される。

```csharp
using Quiver;
using Quiver.Storage.Records;

// DB ディレクトリを開く (無ければ新規作成)。using で必ず Dispose する。
using var db = GraphDatabase.Open(@"C:\data\myapp-graph");

// --- 書き込みトランザクション ---
using (var tx = db.BeginTransaction())
{
    var alice = tx.CreateNode("Person");
    tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));

    var bob = tx.CreateNode("Person");
    tx.SetProperty(bob, "name", PropertyValue.FromString("Bob"));

    tx.CreateRelationship(alice, bob, "KNOWS");

    tx.Commit();   // ← Commit を呼ばずに Dispose すると自動 rollback
}

// --- 読み取りトランザクション ---
using (var tx = db.BeginReadOnlyTransaction())
{
    var stats = db.Diagnostics.GetStatistics();
    Console.WriteLine($"Nodes={stats.NodeCount}, Rels={stats.RelationshipCount}");
}
```

ポイント:

- `using var db = GraphDatabase.Open(dir)` の **Dispose は必須**。Dispose で AutoVacuum ワーカー停止
  → バックエンドの flush/close が行われる。プロセスを `kill` で落としても commit 済みデータは
  WAL replay で復元されるが (→ [04_recovery_troubleshoot.md](04_recovery_troubleshoot.md))、正常終了では必ず Dispose を通す。
- `tx.Commit()` を呼ばないまま `tx` を Dispose すると **rollback** される。これが既定の安全側挙動。
- `GraphDatabase` インスタンスは **スレッドセーフ**。複数スレッドから同時に `BeginTransaction` してよい。
  ただし 1 つの `tx` を複数スレッドで共有してはいけない。

---

## 2. スキーマと索引を用意する

ラベル / リレーションシップタイプ / プロパティキーは **使用時に自動で登録される** ので、
事前のスキーマ宣言は必須ではない。ただし **検索や MERGE を高速化する索引は明示的に作る**。

```csharp
using var db = GraphDatabase.Open(dir);

// 起動直後に一度だけ索引を作る (冪等。既にあれば no-op)。
db.Schema.CreateIndex(
    indexName: "idx_person_email",
    label: "Person",
    propertyKey: "email",
    kind: IndexKind.StringEquality);
```

索引を作っておくと:

- プロパティ等価検索 (`Has("email", ...)`) が全スキャンから O(log n) シークになる
- `MergeNode("Person", "email", ...)` が索引シークを使う (索引が無いとラベル内全スキャンに落ち、
  ノード数次第で秒オーダー)

> 大量データ初期投入時は、索引を後から張るより `BeginStreamingBulkLoad(buildAdjacencyIndex: true)`
> を使う方が速い場合がある。[docs/cookbook.md](../cookbook.md) の「BulkLoader」レシピ参照。

---

## 3. appsettings.json + DI で開く (ASP.NET Core / Generic Host)

`Quiver.Hosting` パッケージを足すと、`IConfiguration` から設定を bind して DI コンテナに
`GraphDatabase` を singleton 登録できる。

`appsettings.json`:

```jsonc
{
  "Quiver": {
    "DataDirectory": "C:\\data\\myapp-graph",
    "BufferPoolSize": 536870912,        // 512 MB
    "WalSegmentSize": 67108864,         // 64 MB
    "CheckpointThresholdBytes": 67108864,
    "EnableChecksums": true,
    "Backend": "Binary"
  }
}
```

`Program.cs`:

```csharp
using Quiver;
using Quiver.Hosting;

var builder = WebApplication.CreateBuilder(args);

// "Quiver" セクションを bind して GraphDatabase を singleton 登録。
builder.Services.AddQuiver(builder.Configuration.GetSection("Quiver"));

var app = builder.Build();

app.MapGet("/stats", (GraphDatabase db) =>
{
    var s = db.Diagnostics.GetStatistics();
    return Results.Ok(new { nodes = s.NodeCount, rels = s.RelationshipCount });
});

app.Run();
```

環境変数によるオーバーライドは **二重アンダースコア** 区切り:

```pwsh
$env:Quiver__DataDirectory = "D:\prod\graph"
$env:Quiver__BufferPoolSize = "1073741824"   # 1 GB
```

`Quiver.Hosting` は core の EventSource イベントをホストの `ILoggerFactory` へ自動転送する。
追加のロガー設定は不要で、ASP.NET Core / Generic Host の通常の Logging 設定がそのまま使われる。

`IConfiguration` でbind できない要素 (バックエンドファクトリ・
`LogicalMutationSink` など) を差し込みたい場合は、`AddQuiver` の `postConfigure` デリゲートから
実体 `GraphDatabaseOptions` を直接編集する:

```csharp
builder.Services.AddQuiver(
    builder.Configuration.GetSection("Quiver"),
    postConfigure: opts =>
    {
        opts.DeadlockDetectionInterval = TimeSpan.FromMilliseconds(100);
    });
```

完全な動作サンプルは [`samples/Quiver.Samples.Hosting`](../../samples/Quiver.Samples.Hosting/)。

---

## 4. 設定の主要ノブ (早見表)

詳細とチューニング指針は [03_performance_tuning.md](03_performance_tuning.md)。ここでは「最初に触る順」だけ。

| ノブ | 既定 | まず何を考えるか |
|---|---|---|
| `BufferPoolSize` | 256 MB | working set がメモリに乗るか。乗らないと毎回ディスク I/O。 |
| `CheckpointThresholdBytes` | 64 MB | 大きいほど書き込みは速いが recovery 時間が伸びる。 |
| `CheckpointPolicy` | `Fixed` | `Adaptive` にすると `TargetRecoveryTime` から自動調整。 |
| `GroupCommitWindow` | 0 (無効) | 多並列 commit のワークロードで fsync 回数を削減。 |
| `LockingMode` | `ExclusiveOnly` | read 並列を上げたいなら `ReaderWriter`。 |
| `EnableChecksums` | `true` | 本番は **true 維持**。torn write を検出できる。 |

---

## 5. 再起動して読み戻せることを確認する

組み込み DB の最初の安心材料は「正しく閉じてもクラッシュしても、commit 済みデータが
再オープンで戻る」こと。これを動作確認しておく:

```csharp
NodeId savedId;

using (var db = GraphDatabase.Open(dir))
using (var tx = db.BeginTransaction())
{
    savedId = tx.CreateNode("Config");
    tx.SetProperty(savedId, "version", PropertyValue.FromString("1.0"));
    tx.Commit();
}

// プロセスをまたいでも、別の Open で復元される
using (var db = GraphDatabase.Open(dir))
using (var tx = db.BeginReadOnlyTransaction())
{
    Debug.Assert(tx.NodeExists(savedId));
}
```

クラッシュ (プロセス強制終了) からの復旧挙動は [04_recovery_troubleshoot.md](04_recovery_troubleshoot.md) を参照。

---

## 次に読む

- バックアップを設計する → [02_backup_restore.md](02_backup_restore.md)
- 速度が出ない → [03_performance_tuning.md](03_performance_tuning.md)
- クラッシュ後に起動しない → [04_recovery_troubleshoot.md](04_recovery_troubleshoot.md)
- 採用可否・キャパシティ → [05_known_limits.md](05_known_limits.md)
