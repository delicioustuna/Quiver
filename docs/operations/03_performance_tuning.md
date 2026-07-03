# 03. パフォーマンスチューニング

> **いつ読むか** — 挿入が遅い、検索が遅い、メモリやディスクが想定より使われている、と感じたとき。
> チューニングノブを回す前に、まず最上段の「鉄則」を満たしているか確認すること。
> 設定ノブは [`GraphDatabaseOptions`](../../src/Quiver/GraphDatabase.cs) / appsettings の
> [`QuiverConfigurationOptions`](../../src/Quiver.Hosting/QuiverConfigurationOptions.cs)。

---

## 鉄則: 大量挿入は必ず 1 トランザクションに詰める

**bulk パス (まとめて 1 tx) は ~69 B/entry、per-tx パターン (1 件 1 commit) は約 100 倍遅い。**

これは Quiver で最も効く一手であり、他のどのノブよりも先に守るべき。
根拠は [索引付き書き込みの WAL 増幅](../benchmark-results.md#索引付き書き込みの-wal-増幅):

| パス | EntryCount | WAL bytes/entry | wall time | スループット |
|---|---:|---:|---:|---:|
| **bulk (single tx)** | 100,000 | **69 B** | 963 ms | **~104k inserts/sec** |
| per-tx (1 insert/tx) | 100,000 | 397 B | 112,897 ms | ~886 inserts/sec |
| per-tx (1 insert/tx) | 1,000 | **27,108 B** | 1,227 ms | (極端な WAL 増幅) |

なぜこうなるか:

- 各 commit は変更したページ (NodeStore ページ + 索引ページ + meta ページ) の **PageImage** を WAL に書く。
- bulk パスでは同一ページへの複数変更が **1 つの PageImage に coalesce** されるので、
  entry 数に対してほぼフラットな ~69 B/entry に収まる。
- per-tx パスでは毎 commit ごとに同じページの PageImage を丸ごと書き直すため、小規模では entry あたり
  数十 KB に増幅する。大規模では checkpoint truncation が効いて絶対値は頭打ちになるが、それでも遅い。

### 実践

```csharp
// ✅ GOOD: まとめて 1 tx
using (var tx = db.BeginTransaction())
{
    foreach (var row in rows)
        tx.SetProperty(tx.CreateNode("Item"), "sku", PropertyValue.FromString(row.Sku));
    tx.Commit();
}

// ❌ BAD: 1 件ごとに commit (約 100× 遅い)
foreach (var row in rows)
    using (var tx = db.BeginTransaction()) { /* 1 件 */ tx.Commit(); }
```

- 1 tx が大きすぎてメモリが厳しい場合は、**数千〜数万件単位のチャンク commit** に分ける
  (1 件 1 commit の対極ではなく中間を取る)。
- 初期一括ロード (数百万〜) は `BeginStreamingBulkLoad` を使う ([docs/cookbook.md](../cookbook.md) 参照)。
- 厳密な per-operation atomicity がどうしても要る場合のみ per-tx を選ぶ。スループットは犠牲になると理解する。

---

## バッファプール (`BufferPoolSize`)

既定 256 MB。**working set (頻繁に触るページ集合) がここに乗るか** が読み取り性能を決める。

- 乗っていれば読み取りはメモリヒット。乗らないと毎回ディスク I/O に落ちる。
- ヒット率は実測する:

```csharp
var s = db.Diagnostics.GetStatistics();
double hitRatio = (double)s.BufferPoolHits / (s.BufferPoolHits + s.BufferPoolMisses);
```

  または `dotnet-counters` の `buffer-pool-hit-ratio` ([docs/cookbook.md](../cookbook.md) §9)。
- ヒット率が低く RAM に余裕があるなら増やす。目安は **データ総量 or hot 部分が収まる量**。
- 増やしすぎると GC 圧と OS メモリ圧を招く。物理 RAM とアプリの他用途を踏まえて上限を決める。

---

## Vector payload slab cache (`VectorCacheBudgetBytes`)

既定 64 MB。全 vector index で共有する上限で、HNSW 距離計算時の payload page pin と
ベクトルコピーを避ける。約 64 KiB の slab を seq range ごとに遅延確保するため、
削除や世代混在で seq が疎でも単一の巨大配列は確保しない。

```csharp
var options = new GraphDatabaseOptions
{
    VectorCacheBudgetBytes = 128L * 1024 * 1024,
};
```

- vector 検索が主要 workload で RAM に余裕があれば、hot vector 集合が収まるまで増やす。
- 複数 index の合計予算なので、index 数を増やしても指定値を超えて常駐しない。
- 0 以下で無効。予算超過時は永続ページへフォールバックし、検索結果は変わらない。
- legacy 構築グラフの N=20k / dim=768 で 2.76×、新既定の N=10k で 2.07×。詳細は
  [payload slab cache](../benchmark-results.md#payload-slab-cache) を参照。

---

## KNN 探索幅 (`VectorSearchOptions`)

HNSW の検索精度とレイテンシは `VectorSearchOptions.EfSearch` で調整できる。
既定値は従来互換の 200。値を下げると探索候補が減って高速になるが、近傍の取りこぼしが増える。

```csharp
var searchOptions = new VectorSearchOptions
{
    EfSearch = 100,
    FilteredOversampleFactor = 8,
};

using var cursor = db.Vectors.KnnSearch("embedding", query, k: 10, searchOptions);
var batch = db.Vectors.KnnSearchBatch("embedding", queries, k: 10, searchOptions);

var traversal = tx.G(db.Schema)
    .Knn("embedding", query, k: 10, searchOptions);
```

固定 corpus（N=10,000、dim=384、cosine、20 queries、旧構成 M=16、Mmax0=32、efConstruction=200）
での実測は次のとおり。latency は HNSW カーソル生成と全件列挙だけを warmup 後に計測した平均値。

| EfSearch | recall@10 | 平均 latency |
|---:|---:|---:|
| 32 | 0.245 | 0.281 ms |
| 64 | 0.435 | 0.431 ms |
| 100 | 0.570 | 0.649 ms |
| 200（既定） | 0.825 | 1.023 ms |

この sweep は構築品質が低い旧構成で、efSearch だけでは 0.95 に届かないことを示す。
現行既定は M=32 / Mmax0=64 / efConstruction=400（recall 0.950）。
latency は payload cache 導入後の値。この corpus では低い `EfSearch` の recall 低下が大きい。
精度要件を測らずに既定値を下げないこと。
`FilteredOversampleFactor` はフィルタ付き HNSW の探索幅を `k × 係数` まで広げる。
候補集合が小さく exact gather 経路を選ぶ場合、この値は結果や計算量に影響しない。

---

## WAL とチェックポイント

### `CheckpointThresholdBytes` (既定 64 MB)

前回 checkpoint 以降に WAL がこのバイト数以上成長し、かつアクティブ tx が 0 になった時点で、
全データページをフラッシュして WAL を truncate する。

- **大きく** すると: checkpoint 頻度が下がり書き込みは速くなるが、**recovery で再生する WAL が増え
  起動が遅くなる**。WAL ファイルも大きくなる。
- **小さく** すると: recovery は速いが checkpoint が頻発し書き込みスループットが落ちる。
- `0` 以下にすると checkpoint を行わず WAL は単調増加する (基本使わない)。

### 適応チェックポイント (`CheckpointPolicy`, 既定 `Fixed`)

手で threshold を決めたくない場合は `Adaptive` にする。
直近 `AdaptiveSampleWindow` 件 (既定 1000) の bytes/tx 移動平均から、`TargetRecoveryTime` (既定 5 秒) を満たす threshold を周期的に再計算する。

```csharp
var opts = new GraphDatabaseOptions
{
    CheckpointPolicy = CheckpointPolicy.Adaptive,
    TargetRecoveryTime = TimeSpan.FromSeconds(3),     // 起動を 3 秒以内に抑えたい
    MinCheckpointThresholdBytes = 4L * 1024 * 1024,   // 病的な過剰 checkpoint を防ぐ下限
    MaxCheckpointThresholdBytes = 1024L * 1024 * 1024 // WAL 肥大を防ぐ上限
};
```

- 「recovery 時間を SLA に収めたい」ときは Adaptive + `TargetRecoveryTime` が素直。
- ワークロードが安定していて手で測れるなら `Fixed` + 実測値でも良い。

### `WalSegmentSize` (既定 64 MB)

WAL 1 セグメントのサイズ。極端に小さくするとセグメントローテーションが頻発する。通常は既定で良い。

---

## グループコミット (`GroupCommitWindow`, 既定 0 = 無効)

**多数のスレッドが並列に commit する** ワークロード (Web API で各リクエストが小さな tx を commit する等)
で効く。最初の commit 到着からこの window 経過まで待って後続 commit を貯め、まとめて 1 回の fsync で処理する。

```csharp
var opts = new GraphDatabaseOptions
{
    GroupCommitWindow = TimeSpan.FromMicroseconds(500)   // 推奨 100µs〜1ms
};
```

- 効果: fsync 回数が激減し IOPS を節約。並列度が高いほど効く。
- 代償: 単一 commit のレイテンシが (fsync 時間 + window) まで増える。
- **単一スレッドで逐次 commit するワークロードでは効果が無い** (貯める相手がいない) のでむしろ有害。
  並列 writer がいるときだけ有効化する。
- 内部は Stopwatch + SpinWait の busy-wait で sub-ms 精度を確保 (Windows の `Task.Delay` ~15ms 解像度を回避)。

---

## ロック戦略 (`LockingMode`, 既定 `ExclusiveOnly`)

- `ExclusiveOnly` (既定): 読み取りロックを取らない現挙動。read-heavy でも reader 同士が
  X ロックを取り合うと並列度が出ない。
- `ReaderWriter`: 読み取りを Shared、書き込みを Exclusive ロックにする。**複数 reader が同時に進める** ので、
  read が支配的なワークロードでスループットが上がる。

```csharp
var opts = new GraphDatabaseOptions { LockingMode = LockingMode.ReaderWriter };
```

関連ノブ:

- `LockTimeout` (既定 5 秒): ロック取得の上限。短くすると詰まりを早く検知できるが、正常な待ちも
  打ち切ってしまう。
- `DeadlockDetectionInterval` (既定 null = 無効): 設定すると wait-for graph を周期的に取り Tarjan SCC で
  デッドロックを検出し、最も若い tx を犠牲にして `DeadlockException` で中断する。推奨 **100ms**
  (検出遅延が短く CPU オーバーヘッドも 1% 未満を狙える)。無効のままだと `LockTimeout` でしか抜けられない。

---

## チェックサム (`EnableChecksums`, 既定 `true`)

ページの torn write / ビット腐敗を検出する。**本番では true 維持を強く推奨**。
極端に CPU が逼迫し、かつストレージが信頼できる環境でのみ無効化を検討するが、データ破損の早期検出を
失うトレードオフを理解した上で。

---

## 索引を張る

検索や MERGE が遅いとき、まず該当プロパティに索引があるか確認する。

```csharp
db.Schema.CreateIndex("idx_person_email", "Person", "email", IndexKind.StringEquality);
```

- 索引が無いプロパティ等価検索はラベル内全スキャン。ノード数に比例して遅くなる。
- `MergeNode` も索引が無いと全スキャンに落ちる。業務キーには必ず索引を ([docs/cookbook.md](../cookbook.md) §2)。
- ただし索引は書き込みコスト (WAL 増幅・更新) を増やす。**検索する列にだけ** 張る。

---

## 計測してから回す

推測で回さない。Quiver は観測手段を持っている:

- `db.Diagnostics.GetStatistics()` — ノード/エッジ数、バッファプール hit/miss
- `dotnet-counters -n <proc> --counters Quiver-EventSource` — buffer-pool、WAL、tx、lock、index、vacuum を
  1 秒粒度でライブ観測 ([docs/cookbook.md](../cookbook.md) §9)
- `Quiver.OpenTelemetry` の `AddQuiverInstrumentation()` — OTel でメトリクスとトレースを送る

ボトルネックを 1 つ特定 → 1 ノブだけ動かす → 再計測、を繰り返すこと。

---

## チューニング早見表

| 症状 | まず疑うノブ |
|---|---|
| 一括挿入が異常に遅い | **per-tx になっていないか** (鉄則) → 1 tx / チャンク commit に |
| 読み取りが遅い・ディスク I/O 多い | `BufferPoolSize` を増やす / 索引を張る |
| 起動 (recovery) が遅い | `CheckpointThresholdBytes` を下げる / `Adaptive` + `TargetRecoveryTime` |
| 並列 commit で fsync が頭打ち | `GroupCommitWindow` を 100µs〜1ms |
| read 並列が出ない | `LockingMode = ReaderWriter` |
| KNN の latency / recall を調整したい | `VectorSearchOptions.EfSearch` を実測しながら変更 |
| ロックで詰まる・デッドロック疑い | `DeadlockDetectionInterval = 100ms`、`LockTimeout` 見直し |
| 特定プロパティ検索が遅い | `Schema.CreateIndex` |
