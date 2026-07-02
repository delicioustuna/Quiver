# Quiver アーキテクチャ概要

Quiver の全体構成、データの流れ、デプロイモデル、運用上の注意をまとめる。
内部実装の詳細は [開発者向けドキュメント](design/development.md) と [仕様](spec/) を参照。

---

## 技術スタック

| 区分 | 技術 |
|---|---|
| 言語 | C# 13 / .NET 10 |
| 外部ランタイム依存 | `System.IO.Hashing`（CRC32C）、`Microsoft.Extensions.Logging.Abstractions` |
| AOT 対応 | NativeAOT publish（リフレクション不使用） |

ストレージエンジン、WAL、リカバリ、バッファプール、B+Tree、HNSW、全文インデックス、BM25 スコアリング、MVCC、クエリ最適化、Volcano オペレータは、すべてサードパーティ依存ゼロのマネージド C# で実装されている。

## レイヤ構成

エンジン中核は単一アセンブリ `Quiver` に集約される。
オプションパッケージ（`Quiver.Rag` 等）はエンジンに依存するが、エンジンはそれらに依存しない。

```
アプリケーション
  │
  ├─ Quiver.Rag           RAG 高レベル API（取込、検索）
  ├─ Quiver.Hosting        DI 統合
  ├─ Quiver.OpenTelemetry  計装
  │
  ▼
Quiver（エンジン中核）
  ├─ GraphDatabase         ファサード
  ├─ GraphTransaction      CRUD、走査、インデックス操作
  ├─ Query Engine          論理 IR → 最適化 → 物理オペレータ
  ├─ Transaction Manager   MVCC、ロック、リカバリ、チェックポイント
  ├─ Index                 B+Tree、全文インデックス
  ├─ Vector                永続ベクトルストア + HNSW
  ├─ WAL                   Write-Ahead Log
  └─ Storage               ページ管理、バッファプール
       │
       ▼
   *.quiver + *.quiver-wal
```

## ファイルレイアウト

静止時は `*.quiver` 1 ファイルにすべてのデータが格納される。
稼働中は WAL サイドカー `*.quiver-wal` が隣に存在する。
クリーンシャットダウン後は WAL は空になるか存在しない。

## デプロイモデル

Quiver は in-process の組み込み DB であり、サーバプロセスは存在しない。

```
┌──────────────────────────────┐
│  ホストアプリケーション        │
│    ├─ GraphDatabase.Open()   │
│    ├─ ビジネスロジック         │
│    └─ db.Dispose()           │
│                              │
│  *.quiver + *.quiver-wal     │
│  (ローカルファイルシステム)     │
└──────────────────────────────┘
```

`*.quiver` ファイルは排他ロック（`FileShare.None`）で開かれるため、同一ファイルを複数プロセスから同時に開くことはできない。
マルチプロセスが必要な場合は、上位に gRPC や HTTP のラッパを配置する。

NativeAOT に対応しているため、`dotnet publish -c Release -r <rid> /p:PublishAot=true` で単一バイナリとして配布できる。

## 書き込みの流れ

1. `db.BeginTransaction()` でトランザクションを開始する
2. `CreateNode`、`SetProperty` 等で変更を加える。変更はバッファプール上のページに反映される
3. `tx.Commit()` で WAL に Commit レコードを書き、`fsync` で永続化する
4. チェックポイント条件に達すると、dirty ページがデータファイルに書き戻される

`Commit()` が返った時点でデータは永続化されている。
プロセスの kill や電源喪失の後でも、再起動時にリカバリが自動実行される。

## 読み取りの流れ

1. `db.BeginReadOnlyTransaction()` で開始時点のスナップショットを取得する
2. スキャンやインデックス検索で読み取る行は、MVCC 可視性チェックを通る
3. リーダはライタをブロックせず、ライタもリーダをブロックしない

スナップショットは開始時点のものであり、その後にコミットされた書き込みは観測しない。
より新しい状態を見るには、新しいトランザクションを開く。

## ハイブリッド検索の流れ（RAG）

`RagSearcher.SearchAsync` は以下の手順で検索する。

1. BM25 全文検索で term ベースの top-k を取得する
2. KNN ベクトル検索で embedding ベースの top-k を取得する
3. RRF（Reciprocal Rank Fusion）で両結果をマージする
4. ヒットしたチャンクから `NEXT_CHUNK`、`HAS_CHUNK` を辿り、前後文脈と親文書を付与する

手順 4 の graph expansion が、ベクトル DB にはない Quiver の差別化ポイントである。

## 運用上の注意

### 書き込みの性能

大量挿入は 1 トランザクションにまとめる。
1 件ごとに commit するパターンは、バッチ挿入より約 100 倍遅い。

さらに高速化するなら `BulkLoader` を使う（通常 TX 比 ~12× 高速）。

並行書き込みでは `GraphDatabaseOptions.GroupCommitWindow` を設定すると、複数トランザクションの WAL フラッシュを 1 回の fsync にまとめられる。

### 読み取りの性能

隣接インデックス（`buildAdjacencyIndex: true`）を構築すると、1-hop 走査が約 30 倍高速になる。
読み取り主体のワークロードでは隣接インデックスの構築を推奨する。

### トランザクションの利用規約

- `GraphDatabase` インスタンスはスレッド間で共有して使い回す（スレッドセーフ）
- トランザクションは 1 スレッドで開始、使用、commit/dispose する（スレッドアフィン）
- `Begin` と `Commit` の間で `await` しない
- 書き込みの直列化はアプリケーション側の責任（`SemaphoreSlim(1,1)` 等）
- トランザクションは短く保つ。長時間のオープンは WAL ファイルの増大を招く

### バックアップ

稼働中でもライブスナップショットを取得できる。
`Dispose()` 後のコールドコピー（`*.quiver` をコピー）も可能。
詳細は [バックアップと復元](operations/02_backup_restore.md) を参照。

### 障害復旧

クラッシュ後の `GraphDatabase.Open()` で 2 フェーズリカバリが自動実行される。
コミット済みの変更は復元され、未コミットの変更は巻き戻される。
詳細は [リカバリとトラブルシュート](operations/04_recovery_troubleshoot.md) を参照。

## 計測と監視

`Quiver.OpenTelemetry` パッケージで OpenTelemetry の trace と metrics を登録できる。
`Microsoft.Extensions.Logging.Abstractions` 経由の構造化ログにも対応する。
