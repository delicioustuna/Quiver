# 既知の限界

> as-built 仕様（QUIVER-SW family version 2、2026-07-18）

本書はエンジンの現時点での既知の限界を記す。v1 統合監査で発見・修正された欠陥はここでは追跡しない
— それらは回帰テストと git 履歴でカバーされる。

## 並行性・スレッディング・安全な利用 {#concurrency}

エンジンは **書き込みトランザクションを 1 度に 1 つだけ、任意数の並行リーダと並走させて** 動作する。
以下のルールがサポートされるコントラクトであり、これに従えばデータベースは正しく、かつクラッシュ安全に保たれる。

### プロセスとスレッディングのルール {#threading}

- **データベースごとに 1 プロセス。** `*.quiver` ファイルは排他 OS ファイルロック (`FileShare.None`) で
  開かれる。2 つ目のプロセスはこれを開けない。マルチプロセスやネットワークアクセスは存在しない —
  それが必要なら自前のサービスを前段に置くこと。
- **データベースごとに 1 つの `QuiverDatabase` をスレッド間で共有する。** インスタンスはスレッドセーフ。
  一度開いてプロセスのライフタイムを通じて再利用すること。同一プロセス内で同じファイルを 2 度開かないこと。
- **トランザクションハンドルは同時使用不可である。** write 文脈と MVCC 文脈は非同期フローに保持されるため、
  `await` の継続や、重ならない `Task.Run` 越しの利用で WAL ロギングが暗黙に欠落することはない。
  ただし同じ `IReadTransaction` または `IWriteTransaction` ハンドルを複数スレッドから同時に使うことは未サポートであり、
  検出された場合は `TransactionException` をスローする。トランザクションから取得したカーソルや列挙子も、
  トランザクション有効期間内に 1 つの操作フローで消費すること。

### ライタは 1 つ、リーダは並行 {#one-writer}

- **書き込みトランザクションは 1 度に 1 つだけ進行できる。** エンジンは内部 writer gate により
  `BeginWriteTransaction()` を直列化する。2 本目の書き込みトランザクションは既定で先行 writer の終了を
  `QuiverDatabaseOptions.LockTimeout` まで待ち、期限を超えると `TransactionException` をスローする。
  `QuiverDatabaseOptions.EnforceExclusiveWriter` を有効にすると待機せず即時に `TransactionException` をスローする。
  scalar index definition と全文 manifest の publish も同じ書き込み排他に従う。
  immutable全文 artifact の構築は read snapshot で行うため、構築中も通常 writer は進行できる。
- **リーダはブロックせず、ブロックもされない。** `BeginReadTransaction()` は開始時の一貫した
  コミット済みスナップショットを取得し（snapshot isolation）、デフォルトではロックを取得しない。
  任意数のリーダがそれぞれのスレッド上で、単一のライタと並行して並列に動作する。リーダは自身の開始後に
  コミットされた書き込みを観測しない — より新しい状態を見るには新しいリーダを開くこと。

### トランザクションは短く保つ {#short-transactions}

チェックポイント、`Vacuum()`、WAL の切り詰めは、**アクティブなトランザクションが無い**とき
(`ActiveCount == 0`) にのみ実行される。そして最も古いオープン中のトランザクションが WAL 切り詰めの
境界をピン留めする。開いたままのトランザクションは — **読み書きを問わず** — したがって WAL 切り詰めと
領域回収をブロックし、保持されている間 WAL ファイルが増大し続ける。トランザクションを開き、作業を行い、
速やかに commit または dispose すること。ユーザの思考時間・UI イベント・ネットワーク呼び出しをまたいで
トランザクションを開いたままにしないこと。

### コミットと永続性 {#commit-durability}

`Commit()` は WAL が fsync された後にのみ返る。返った後は、データはプロセスの kill や電源喪失を生き延びる
（再オープン時にリカバリが再生する — [02_wal_recovery.md](02_wal_recovery.md) を参照）。`Commit()` なしで
dispose されたトランザクション（例外が `using` スコープを巻き戻す場合も含む）はロールバックされる。
部分適用されたトランザクションが可視になることは決してない。

この永続性保証は binary backend に適用される。インメモリバックエンド
（`QuiverDatabase.CreateInMemory()` / `":memory:"`）の commit は同一インスタンス内の可視性と
rollback 原子性だけを保証し、プロセス終了やデータベースの破棄・再オープンを跨いでデータを保持しない。
スナップショットと vacuum もサポート対象外である。

### 競合時のリトライ {#retry}

複数スレッドから書き込みを駆動する場合、writer gate / ロック競合は待機中のトランザクションを
`TransactionException`（writer gate またはロック待ちタイムアウト）でアボートする。
これは *一時的* である: アボートされたトランザクションは
永続的な変更を何も行っていないため、小さな有界バックオフを挟んで **トランザクション全体** を
リトライすること（部分的にではなく）:

```csharp
T WithRetry<T>(Func<T> runTxn, int maxAttempts = 5)
{
    for (int attempt = 1; ; attempt++)
    {
        try { return runTxn(); }
        catch (Exception e) when (
            e is DeadlockException or TransactionException
            && attempt < maxAttempts)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(2 * attempt)); // back off, retry whole txn
        }
    }
}
```

### 書き込みの直列化 {#write-serialization}

書き込みゲートはエンジン内に組み込まれているため、アプリケーション側で `BeginWriteTransaction()` を
さらに `SemaphoreSlim` で囲む必要はない。高頻度の書き込みを扱うアプリケーションでは、専用の
ライタキューでジョブを集約すると、自然なバッチングとリトライ制御を実装しやすい。

読み取りにゲートは不要: `BeginReadTransaction()` を任意のスレッドで開き、互いに、そしてライタと
並行して実行できる。

複数の書き込みトランザクションを同時に進行させる機能はサポートしない。

## Nexusの契約と限界 {#nexus-limits}

第一級Nexusは次の契約で動作する。いずれも v1 の設計判断であり、緩和は実需が出てから検討する。

### メンバー集合は作成時確定 {#nexus-immutable-members}

Nexusのメンバー集合（ロールとVertexの組）は `CreateNexus` の時点で確定し、
以後変更できない。変更は削除 + 再作成で表現する。プロパティは作成後も変更できる。

**設計根拠**: メンバー集合が不変であることで、「型 + ロール付きメンバー集合」による同一性が
well-defined になり、incidence チェーンの構築を作成時の一括処理にでき、
逆方向リンクの常時維持も不要になる。ストレージとロック設計の大部分がこの前提に立つ。

### メンバーはVertexのみ、アリティ 2 以上 {#nexus-vertex-members}

v1 のメンバーは `VertexId` に限る。EdgeやNexus自身をメンバーにする
高階の入れ子（RDF-star 的な拡張）はサポートしない。アリティ（メンバー数）は 2 以上を要求し、
同じロールとVertexの組は 1 つのNexus内で重複できない。

### メンバーの列挙順序は保証しない {#nexus-member-order}

`GetMembers` と DSL の `Members` は、作成時に渡したメンバーの順序を保存しない。
順序が意味を持つ場合は、ロール名（`first` / `second` など）またはメンバーVertexのプロパティで表現する。

### Edgeとの相互変換 API は対象外 {#nexus-no-conversion}

Nexus走査からEdgeを生成する API、およびその逆
（reified パターンからの移行を含む）は v1 では提供しない。必要な場合はアプリケーション側で
走査結果から明示的に作成する。このとき導出したEdgeは元のNexusと系譜を同期しない
（元の削除は導出先に波及しない）。冪等な更新は再実行と `MergeEdge` で行う。

### Vertex削除の高次数カスケード {#nexus-delete-cascade}

`DeleteVertex` は、そのVertexが属すすべてのライブNexusを 1 度ずつカスケード削除する。
このコストは所属Nexus数に比例する。実測（arity 4、AMD Ryzen 7 5700X）では
1 Vertexが 10^3 / 10^4 個のNexusに属す状態の削除がトランザクション時間
7.84 ms / 47.69 ms、WAL 61 KB / 608 KB で完走し、デッドロックや整合性違反は生じない。
RAG の Chunk や頻出エンティティのような高次数Vertexを大量に削除するバッチでは、
トランザクションを分割して WAL 切り詰めの余地を与えること
（[§short-transactions](#short-transactions) 参照）。

### 整合性チェックは書き込み停止時を想定 {#nexus-consistency-check}

`CheckConsistency` は複数ストアをロックなしで走査するため、同時更新中は一時的な不整合を
観測しうる。診断は書き込みを止めた状態で実行すること。

## BM25 コーパス統計はスナップショットベース {#bm25-stats}

BM25 スコアリングはコーパス文書数 `N`、平均文書長 `avgdl`、term ごとの `df` を同じ visible segment snapshot から取得する。
同じ manifest generation の postings、norms、stats は読み取り専用 snapshot cache として再利用する。
`GraphStats` を明示的に与えた場合は取得時点の近似統計を使う。

WAND top-k 枝刈りは term ごとの保守的な上限 `idf * (K1 + 1)` を用いる。
上限または stats の鮮度から正しさを証明できない場合は strict scan へ fallback する。

**設計根拠**: スコアリングのたびに segment 全体を再materializeするとクエリレイテンシが文書数に比例して悪化する。
immutable manifest 単位の cache は old/new reader の統計を混ぜずに再計算を避ける。

**緩和策**: `GraphStats` を検索へ与える場合は、書き込みバッチ後に取り直して WAND の snapshot 統計を更新する。

**将来方針**: reader horizonを越えた旧全文 segment の物理回収は segment GC で行う。

## Derived全文 segment の再構築 {#fulltext-segment-rebuild}

正常 reopen は persisted manifest から `*.quiver-ftseg` の checksum 一致 body を開き、primary scan を行わない。
referenced body の欠損または checksum 不一致で `RebuildRequired` になった場合だけ、全文検索は transaction-local primary property scan へ fallback する。
fallback artifact は global manifest として公開せず、background worker が source generation を再検証してから publish する。
この破損時 fallback は結果集合を保つが、publish 完了までは検索レイテンシが corpus size に比例する。

## Derived vector segment の再構築 {#vector-segment-rebuild}

reopen 直後、merge 中、または derived state が不足する場合、KNN は primary vector property を exact scan する。
この fallback は結果集合を保つが、immutable HNSW segment の publish が完了するまで検索レイテンシが corpus size に比例する。

通常の vector property update は commit-local flat delta segment を公開するため、共有 HNSW グラフを in-place で再リンクしない。
merge worker は writer lease の外で新しい HNSW artifact を構築し、source generation が一致する場合だけ versioned manifest を公開する。
index の drop と再作成は primary vector property を削除しない。

## HNSW 既定パラメタの品質とコスト {#hnsw-default-recall}

既定の構築パラメタは M=32 / Mmax0=64 / efConstruction=400。dim=384 / N=10,000 /
cosine の決定的コーパスで true recall@10 **0.950**、30% 削除後 **0.985** を満たす。

旧既定 M=16 / Mmax0=32 / efConstruction=200 は recall 0.825、構築 5.95 秒、
検索 1.02 ms。新既定は構築 10.00 秒 (+68%)、検索 1.43 ms (+41%) だが、
ローカル RAG の既定品質目標 0.95 を満たすため、このコストを採用した。
より軽い構築を優先する利用者は `VectorIndexDefinition` の HNSW parameter で旧値相当を明示できる。

**設計根拠**: efSearch=200 まで広げても旧構築グラフは 0.825 止まりで、検索時パラメタだけでは
0.95 に届かない。payload cache 導入後は新既定の 1.51 ms も導入前の旧既定 2.21 ms より速い。
`Quiver.Benchmarks.RecallCheck` は旧構成を比較基準、新既定を 0.95 SLA ゲートとして維持する。

## 自動マイグレーションなし {#no-migration}

QUIVER-SW family version 2 ではないデータベースは `StorageFormatMismatchException` で拒否する。
旧 WAL は `WalFormatMismatchException` で拒否する。
自動 migration と互換 reader は存在しないため、データベースは source data または logical export から作り直す。

**設計根拠**: オンディスクフォーマットのマイグレーションは、全ページの読み書きとバリデーションが
必要であり、データ破損リスクが高い。Quiver の主要ユースケース（ローカル RAG）ではソースデータ
（元文書）が常に利用可能であるため、再構築コストはマイグレーションの複雑さと信頼性リスクに
見合わない。SQLite も同様に手動 dump + restore を推奨するアプローチを取っている。

**緩和策**: フォーマット変更を含むアップグレード時は以下の手順を踏む:
1. ソースデータから新フォーマットで DB を再構築する（BulkLoader を活用）
2. 旧 DB ファイルは rollback 用にバックアップとして保持する

スキーマレベルの変更（ラベル名変更・プロパティキー追加等）は `IMigration` API
でサポートされる。ここで言う「自動マイグレーションなし」は
オンディスクの物理フォーマット変更のみを指す。

**将来方針**: 1.x 内では QUIVER-SW family version を固定する。
MAJOR バージョンアップ時には migration tool の提供を検討する
（[api-stability.md §4](../api-stability.md#4-ファイル--wal-フォーマット互換性) 参照）。

## In-Process のみ {#in-process}

Quiver はアプリケーションプロセス内で動作する。サーバモード・ネットワークプロトコル・プロセス間
アクセスは存在しない。`*.quiver` ファイルは排他ファイルロック (`FileShare.None`) で開かれる。

**設計根拠**: 組み込み DB として SQLite / LiteDB と同じポジションを取る設計判断。ネットワーク層を
持たないことで、シリアライゼーションオーバーヘッド・接続管理・認証・TLS の複雑さを排除し、
レイテンシを最小化する。単一プロセスモデルはトランザクションの一貫性保証を大幅に簡素化し、
分散合意プロトコルを不要にする。

**緩和策**: マルチプロセスアクセスが必要な場合は、アプリケーション側で以下のいずれかを採用する:
- 単一のホストプロセスが DB を開き、gRPC / HTTP 等で他プロセスにサービスを提供する
- 読み取り専用アクセスのみの場合、`CreateSnapshot()` で取得したバックアップファイルを別プロセスで開く

**将来方針**: サーバモードの追加は 1.x のスコープ外。将来的にコミュニティ需要があれば、
`Quiver.Server` パッケージとして Quiver の上に薄い gRPC / HTTP ラッパを別リポジトリで
提供する可能性があるが、コアエンジンは組み込み専用を維持する。

## チェックポイントによる WAL 切り詰め {#wal-truncation}

WAL の切り詰めは、完全なチェックポイント (Begin + End) の後にのみ発生する。アプリケーションが
チェックポイントなしで長時間動作する場合（例: 非常に大きく長時間実行されるトランザクション）、
WAL ファイルは際限なく増大する。

**設計根拠**: WAL の切り詰めにはチェックポイントの完全性（Begin + End sentinel が揃っている）の
保証が必要であり、不完全なチェックポイントで切り詰めるとクラッシュ時にデータを喪失する。
また、オープン中のトランザクションが参照する WAL レコードを切り詰めると、そのトランザクションの
スナップショット一貫性が崩壊する。これらの安全性保証を維持するために、切り詰めの前提条件は
保守的に設定されている。

**緩和策**:
- トランザクションを短く保つ（[§short-transactions](#short-transactions) 参照）
- `CheckpointPolicy.Adaptive`（既定）を使う: WAL サイズがしきい値を超えると自動でチェックポイントが走る
- 長時間バッチ処理ではバッチを小さいトランザクションに分割し、各 commit 後にチェックポイントの余地を与える
- `AutoVacuum` を有効にすると、アイドル時に定期的にチェックポイント + vacuum が実行される

**将来方針**: 1.x では `CheckpointPolicy.Adaptive` の自動しきい値調整（実装済み）に
より、通常ワークロードでは WAL の際限なき増大は起きない。極端なケース（単一トランザクションで
数百万件書き込み）への対応として、トランザクション内チェックポイント（savepoint 境界での
部分切り詰め）を将来検討する。
