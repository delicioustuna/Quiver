# 05. 既知の制約 (Known Limits)

> **いつ読むか** — Quiver を採用してよいか判断するとき、キャパシティを見積もるとき、
> 「これはできるはず」と思った操作が見当たらないとき。Quiver は **単一プロセスに埋め込む
> 組み込みグラフ DB** であり、分散データベースではない。ここを取り違えると運用設計を誤る。

---

## アーキテクチャ上の前提

### 単一プロセス embedded

Quiver は「ライブラリとしての DB」。アプリと同じプロセス内で動く。

- **サーバープロセスは無い**。ネットワークポートも listen しない。リモートから直接接続する手段は無い。
  外部公開したいなら、アプリ側で HTTP/gRPC API を立てて Quiver をその裏に置く
  ([`samples/Quiver.Samples.Hosting`](../../samples/Quiver.Samples.Hosting/) 参照)。
- **1 つの DB ディレクトリは 1 プロセスからのみ開ける**。複数プロセスからの同時オープンは不可。
  同一プロセス内では `QuiverDatabase` を singleton 共有し、複数スレッドから使う (インスタンスはスレッドセーフ)。
- **並行モデルは「単一ライタ + 並行リーダ」**。読み取りはスナップショット分離でロックフリーに並行でき、
  書き込み中でも読める。書き込みは 1 度に 1 tx を前提とするため、複数スレッドから書く場合はアプリ側で
  直列化する (`SemaphoreSlim(1,1)` ゲート、または専用ライタスレッド + キュー)。
  トランザクションはスレッド親和で、生成したスレッドで使い切る (別スレッドへ渡さない)。
  tx は短く保つ。開いたままだと checkpoint、`Vacuum`、WAL 切り詰めが止まり WAL が肥大する。
  並行性とスレッドの規約、リトライ実装例は
  [docs/spec/08_known_limits.md#concurrency](../spec/08_known_limits.md#concurrency) に集約。

### レプリケーション無し

- **組み込みのレプリケーション / HA / 自動フェイルオーバーは無い**。
- 冗長化は運用側で組む: 定期スナップショット ([02_backup_restore.md](02_backup_restore.md)) +
  別ストレージ保管 + 障害時の手動/自動切り替え。
- `LogicalMutationSink` で論理ミューテーションストリームを取り出せるので、将来的な
  レプリケーションや監査ログ転送の足がかりにはできるが、**完成したレプリケーション機能ではない**。

### 認証 / 認可 / 暗号化 無し

- **DB レベルのユーザー認証・アクセス制御は無い**。DB ディレクトリにアクセスできる = 全データに
  アクセスできる。アクセス制御はアプリ層・OS のファイルパーミッションで行う。
- **保存時暗号化 (encryption at rest) は無い**。必要なら OS / ボリュームレベルの暗号化
  (BitLocker / dm-crypt 等) を使う。
- 機密データを扱う場合はファイルシステム権限とディスク暗号化で守ること。

---

## 容量・規模の目安

数値は設計目標および実測ベンチに基づく **目安** であり、ハードウェア・ワークロード・スキーマで
大きく変わる。本番投入前に自分のワークロードで実測すること。

### GA 出口条件 (設計目標)

ロードマップ上の 1.0 GA 出口条件は:

> **100K vertices / 1M edges / 10K concurrent reads (32 thread) を 1 時間連続 +
> chaos injection nightly が 24h 連続グリーン**

これは「この規模・並列度で安定動作することを検証する」という目標であり、**上限ではない**が、
この規模感が快適に動く設計ということ。これを大きく超える規模 (数億Vertex等) は未検証領域。

### 書き込みスループット

- bulk パス (1 tx にまとめる) で **~100k inserts/sec** 程度 ([WAL 増幅計測](../benchmark-results.md#索引付き書き込みの-wal-増幅))。
- per-tx (1 件 1 commit) は約 100 倍遅い。必ず [03_performance_tuning.md](03_performance_tuning.md) の鉄則に従う。

### メモリ

- working set がバッファプール (`BufferPoolSize` 既定 256 MB) に乗るかが読み取り性能を左右する。
  hot データ量に合わせてサイズを決める。

### ディスク

- MVCC により更新/削除は dead version を残すので、論理データ量より物理ファイルは大きくなりがち。
  `Vacuum()` で回収、truncate する ([04_recovery_troubleshoot.md](04_recovery_troubleshoot.md))。
- WAL は checkpoint 設定次第で増減する。recovery 時間とのトレードオフ。

---

## 機能面の制約・注意

### トランザクション / 分離レベル

- 既定は SnapshotIsolation。
  Serializable (SSN) は実験的 API (`[Experimental("QUIVER001")]`) として提供されている。
  Write skew を厳密に排除したいワークロードでの利用を想定するが、安定性保証の対象外である
  ([api-stability.md §5](../api-stability.md) 参照)。
- ロック競合が多い場合は `DeadlockDetectionInterval` を設定しないと `LockTimeout` でしか抜けられない
  ([03_performance_tuning.md](03_performance_tuning.md))。

### vacuum はアクティブ tx 0 が前提

- `Vacuum()` はアクティブトランザクションがあると `Skipped = true` で何もしない。常時書き込みがある
  ワークロードでは回収機会が来ないことがある。
  AutoVacuum ワーカーや低トラフィック時間帯の明示実行を計画する。

### バックエンド差異

- **バイナリバックエンド** が唯一の組み込みバックエンドで、`CreateSnapshot`、`Vacuum`、`CompactAdjacency` 等の運用 API はこれを前提とする。

### 索引

- 索引は明示的に作る必要がある (自動索引は無い)。検索/MERGE する列に `Schema.CreateIndex`。
- 索引数を増やすほど書き込みコスト (WAL 増幅) が上がる。必要な列に絞る。
- abort/crash 後に稀に orphan が残ることがある。`CheckIndexConsistency`、`RepairIndexes` で対処
  ([04_recovery_troubleshoot.md](04_recovery_troubleshoot.md))。

### プラットフォーム

- ターゲットは .NET 10 (`net10.0`)。`AllowUnsafeBlocks` 有効、`System.Threading.Lock` 等の最新 BCL を利用。
- 開発・ベンチは Windows + SSD で実施。他 OS/ストレージでは実測で確認すること。

---

## キャパシティプランニング・ワークシート

採用前に以下を自分のワークロードで埋めると、設定とハードウェアの当たりがつく。

| 項目 | 確認方法 / 決め方 | 効いてくるノブ |
|---|---|---|
| 想定Vertex数 / エッジ数 | ドメインから見積もる。GA 目標 (100K/1M) を大きく超えるなら要実測 | — |
| hot working set のサイズ | 頻繁に触るページ量。全件か一部か | `BufferPoolSize` |
| ピーク書き込み速度 | inserts/sec。bulk か per-tx か (鉄則) | tx 設計 / `CheckpointThresholdBytes` |
| 並列 reader 数 | 同時に読むスレッド数 | `LockingMode = ReaderWriter` |
| 並列 writer 数 | 同時に commit するスレッド数 | `GroupCommitWindow` |
| 許容 recovery 時間 | 起動 SLA (秒) | `CheckpointPolicy = Adaptive` + `TargetRecoveryTime` |
| 許容データ損失 (RPO) | スナップショット間隔を決める | バックアップ周期 ([02](02_backup_restore.md)) |
| ディスク予算 | データ + dead version + WAL + バックアップ世代 | `Vacuum` 周期 / 世代数 |

> 見積もりが終わったら、本番投入前に **(1) 想定規模での連続稼働、(2) リストアリハーサル、
> (3) chaos / クラッシュ注入** の 3 点を検証する。数値はすべてハードウェア依存なので、
> ここの表は「何を測るべきか」のチェックリストとして使い、実値は自環境で取ること。

### サイジングの考え方 (例)

- **物理ディスク** ≈ 実データ量 × (1 + dead version 余裕) + WAL (checkpoint threshold 程度) +
  バックアップ世代数 × スナップショットサイズ。MVCC で dead version が溜まるので、
  論理データ量の数倍を見ておくと安全。`Vacuum` を回せば縮む。
- **RAM** ≈ `BufferPoolSize` + アプリ本体 + GC ヘッドルーム。hot working set が乗らないと
  読み取りが毎回ディスクに落ちるので、ここをケチると体感が悪化する。
- **CPU** — チェックサム (既定 ON) と SIMD ベクトル距離計算が主な消費先。通常はディスク I/O が
  先にボトルネックになる。

## Quiver が「向いている / 向いていない」

| 向いている | 向いていない |
|---|---|
| 単一サービスに埋め込むグラフストア | マルチプロセス/マルチマシン共有 DB |
| 中規模 (〜数百万エンティティ) のグラフ | 数億超の超大規模グラフ (未検証) |
| RAG / 推薦の graph + vector ハイブリッド | 強いマルチテナント認可が DB に要る用途 |
| ローカル / エッジ / オフライン動作 | 組み込み HA・自動フェイルオーバーが必須の用途 |
| バックアップを運用側で管理できる体制 | DB 自身にレプリケーションを期待する用途 |

不確かな点は **自分のワークロードで実測** し、本番前に
[02_backup_restore.md](02_backup_restore.md) のリストアリハーサルと
chaos / 連続稼働の検証を済ませること。
