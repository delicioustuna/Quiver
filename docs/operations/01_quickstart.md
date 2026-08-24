# 01. クイックスタート (運用者向け)

> **いつ読むか** — 初めて Yatagarasu をアプリに埋め込むとき。この 1 ページだけで「DB を作る →
> スキーマ/索引を用意する → 最初の書き込みを commit する → 再起動後に読み戻す」までを通す。
> API レシピの網羅は [docs/cookbook.md](../cookbook.md)、概念解説は [docs/api/](../api/) を参照。

Yatagarasu は **単一プロセスに埋め込む組み込みグラフ DB** であり、
サーバープロセスもネットワークポートも持たない。アプリと同じプロセス内で DB ディレクトリを
1 つ開いて使う。制約の全体像は [05_known_limits.md](05_known_limits.md) を参照。

---

## 1. 最小構成 — 直接 API で開く

NuGet では `Yatagarasu` パッケージ 1 つに依存すればよい。DB は「ディレクトリ」単位で、
存在しなければ初回 `Open` 時に作成される。

```csharp
using Yatagarasu;
using Yatagarasu.Storage.Records;

// DB ディレクトリを開く (無ければ新規作成)。using で必ず Dispose する。
using var db = YatagarasuDatabase.Open(@"C:\data\myapp-graph");

// --- 書き込みトランザクション ---
using (var tx = db.BeginWriteTransaction())
{
    var alice = tx.CreateVertex("Person");
    tx.SetProperty(alice, "name", PropertyValue.FromString("Alice"));

    var bob = tx.CreateVertex("Person");
    tx.SetProperty(bob, "name", PropertyValue.FromString("Bob"));

    tx.CreateEdge(alice, bob, "KNOWS");

    tx.Commit();   // ← Commit を呼ばずに Dispose すると自動 rollback
}

// --- 読み取りトランザクション ---
using (var tx = db.BeginReadTransaction())
{
    var stats = db.Diagnostics.GetStatistics();
    Console.WriteLine($"Vertices={stats.VertexCount}, Edges={stats.EdgeCount}");
}
```

ポイント:

- `using var db = YatagarasuDatabase.Open(dir)` の **Dispose は必須**。Dispose で AutoVacuum ワーカー停止
  → バックエンドの flush/close が行われる。プロセスを `kill` で落としても commit 済みデータは
  WAL replay で復元されるが (→ [04_recovery_troubleshoot.md](04_recovery_troubleshoot.md))、正常終了では必ず Dispose を通す。
- `tx.Commit()` を呼ばないまま `tx` を Dispose すると **rollback** される。これが既定の安全側挙動。
- `YatagarasuDatabase` インスタンスは **スレッドセーフ**。複数スレッドから同時に `BeginWriteTransaction` してよい。
  ただし 1 つの `tx` を複数スレッドで共有してはいけない。

---

## 2. スキーマと索引を用意する

ラベル / Edgeタイプ / プロパティキーは **使用時に自動で登録される** ので、
事前のスキーマ宣言は必須ではない。ただし **検索や MERGE を高速化する索引は明示的に作る**。

```csharp
using var db = YatagarasuDatabase.Open(dir);

// 起動直後に一度だけ索引を作る (冪等。既にあれば no-op)。
using (var schemaTx = db.BeginWriteTransaction())
{
    schemaTx.EditSchema.CreateIndex(new ScalarIndexDefinition(
        "idx_person_email",
        new PropertyTarget(PropertyOwnerKind.Vertex, "email", "Person"),
        IndexKind.StringEquality));
    schemaTx.Commit();
}
```

索引を作っておくと:

- プロパティ等価検索 (`Has("email", ...)`) が全スキャンから O(log n) シークになる
- `MergeVertex("Person", "email", ...)` が索引シークを使う (索引が無いとラベル内全スキャンに落ち、
  Vertex数次第で秒オーダー)

> 大量データ初期投入時は、索引を後から張るより `BeginStreamingBulkLoad(buildAdjacencyIndex: true)`
> を使う方が速い場合がある。[docs/cookbook.md](../cookbook.md) の「BulkLoader」レシピ参照。

---

## 3. appsettings.json + DI で開く (ASP.NET Core / Generic Host)

`Yatagarasu.Hosting` パッケージを足すと、`IConfiguration` から設定を bind して DI コンテナに
`YatagarasuDatabase` を singleton 登録できる。

`appsettings.json`:

```jsonc
{
  "Yatagarasu": {
    "DataDirectory": "C:\\data\\myapp-graph",
    "BufferPoolSize": 536870912,        // 512 MB
    "CheckpointThresholdBytes": 67108864,
    "EnableChecksums": true,
    "Backend": "Binary"
  }
}
```

`Program.cs`:

```csharp
using Yatagarasu;
using Yatagarasu.Hosting;

var builder = WebApplication.CreateBuilder(args);

// "Yatagarasu" セクションを bind して YatagarasuDatabase を singleton 登録。
builder.Services.AddYatagarasu(builder.Configuration.GetSection("Yatagarasu"));

var app = builder.Build();

app.MapGet("/stats", (YatagarasuDatabase db) =>
{
    var s = db.Diagnostics.GetStatistics();
    return Results.Ok(new { vertices = s.VertexCount, rels = s.EdgeCount });
});

app.Run();
```

環境変数によるオーバーライドは **二重アンダースコア** 区切り:

```pwsh
$env:Yatagarasu__DataDirectory = "D:\prod\graph"
$env:Yatagarasu__BufferPoolSize = "1073741824"   # 1 GB
```

`Yatagarasu.Hosting` は core の EventSource イベントをホストの `ILoggerFactory` へ自動転送する。
追加のロガー設定は不要で、ASP.NET Core / Generic Host の通常の Logging 設定がそのまま使われる。

`IConfiguration` でbind できない要素 (バックエンドファクトリ・
`LogicalMutationSink` など) を差し込みたい場合は、`AddYatagarasu` の `postConfigure` デリゲートから
実体 `YatagarasuDatabaseOptions` を直接編集する:

```csharp
builder.Services.AddYatagarasu(
    builder.Configuration.GetSection("Yatagarasu"),
    postConfigure: opts =>
    {
        opts.WriterContentionMode = WriterContentionMode.Wait;
        opts.WriterWaitTimeout = TimeSpan.FromSeconds(2);
    });
```

完全な動作サンプルは [`samples/Yatagarasu.Samples.Hosting`](../../samples/Yatagarasu.Samples.Hosting/)。

---

## 4. 設定の主要ノブ (早見表)

詳細とチューニング指針は [03_performance_tuning.md](03_performance_tuning.md)。ここでは「最初に触る順」だけ。

| ノブ | 既定 | まず何を考えるか |
|---|---|---|
| `BufferPoolSize` | 256 MB | working set がメモリに乗るか。乗らないと毎回ディスク I/O。 |
| `CheckpointThresholdBytes` | 64 MB | 大きいほど書き込みは速いが recovery 時間が伸びる。 |
| `CheckpointPolicy` | `Fixed` | `Adaptive` にすると `TargetRecoveryTime` から自動調整。 |
| `WriterContentionMode` | `Wait` | 先行 writer を待つか、`FailFast` で即時拒否するか。 |
| `WriterWaitTimeout` | 5 秒 | `Wait` 時に writer lease を待つ上限。 |
| `AutoVacuum` | `false` | visibility horizon までの回収を周期実行するか。 |
| `EnableChecksums` | `true` | 本番は **true 維持**。torn write を検出できる。 |

---

## 5. 再起動して読み戻せることを確認する

組み込み DB の最初の安心材料は「正しく閉じてもクラッシュしても、commit 済みデータが
再オープンで戻る」こと。これを動作確認しておく:

```csharp
VertexId savedId;

using (var db = YatagarasuDatabase.Open(dir))
using (var tx = db.BeginWriteTransaction())
{
    savedId = tx.CreateVertex("Config");
    tx.SetProperty(savedId, "version", PropertyValue.FromString("1.0"));
    tx.Commit();
}

// プロセスをまたいでも、別の Open で復元される
using (var db = YatagarasuDatabase.Open(dir))
using (var tx = db.BeginReadTransaction())
{
    Debug.Assert(tx.VertexExists(savedId));
}
```

クラッシュ (プロセス強制終了) からの復旧挙動は [04_recovery_troubleshoot.md](04_recovery_troubleshoot.md) を参照。

---

## 次に読む

- バックアップを設計する → [02_backup_restore.md](02_backup_restore.md)
- 速度が出ない → [03_performance_tuning.md](03_performance_tuning.md)
- クラッシュ後に起動しない → [04_recovery_troubleshoot.md](04_recovery_troubleshoot.md)
- 採用可否・キャパシティ → [05_known_limits.md](05_known_limits.md)
