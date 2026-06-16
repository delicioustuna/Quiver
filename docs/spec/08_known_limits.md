# 既知の限界

> as-built 仕様 (v1 baseline)

本書はエンジンの現時点での既知の限界を記す。v1 統合監査で発見・修正された欠陥はここでは追跡しない
— それらは回帰テストと git 履歴でカバーされる。

## 並行性・スレッディング・安全な利用 {#concurrency}

エンジンは **書き込みトランザクションを 1 度に 1 つだけ、任意数の並行リーダと並走させて** 動作する。
以下のルールがサポートされるコントラクトであり、これに従えばデータベースは正しく、かつクラッシュ安全に保たれる。

### プロセスとスレッディングのルール {#threading}

- **データベースごとに 1 プロセス。** `*.quiver` ファイルは排他 OS ファイルロック (`FileShare.None`) で
  開かれる。2 つ目のプロセスはこれを開けない。マルチプロセスやネットワークアクセスは存在しない —
  それが必要なら自前のサービスを前段に置くこと。
- **データベースごとに 1 つの `GraphDatabase` をスレッド間で共有する。** インスタンスはスレッドセーフ。
  一度開いてプロセスのライフタイムを通じて再利用すること。同一プロセス内で同じファイルを 2 度開かないこと。
- **トランザクションはシングルスレッドかつスレッドアフィンである。** トランザクション — およびそこから
  取得したカーソルや列挙子 — は、すべて 1 つのスレッド上で作成・使用しなければならない。その write 文脈と
  MVCC 文脈はスレッドローカル (`[ThreadStatic]`) であるため、生きたトランザクションを別スレッドに
  渡すこと（`Task.Run`、別スレッドで再開する `await` 継続、`Parallel.For` など）はサポートされず、
  WAL ロギングを暗黙にスキップしうる。トランザクションは 1 つのスレッド上の 1 つの同期スコープ内で
  開始・使用・commit/dispose すること。`Begin` と `Commit` の間で `await` しないこと。

### ライタは 1 つ、リーダは並行 {#one-writer}

- **書き込みトランザクションは 1 度に 1 つだけ進行できる。** エンジンは 2 つ目の並行ライタを
  `BeginTransaction()` で *拒否しない* — 書き込みの直列化はアプリケーションの責任である
  （[書き込みの直列化](#write-serialization) を参照）。さらに、すべての二次インデックスと全文の
  mutation は単一のグローバルインデックスロックに集約されるため、ロックモードに関わらず、2 つの
  トランザクションがインデックス / postings を同時に mutation することは決してない。
- **リーダはブロックせず、ブロックもされない。** `BeginReadOnlyTransaction()` は開始時の一貫した
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

### 競合時のリトライ {#retry}

複数スレッドから書き込みを駆動する場合、ロック競合は敗者トランザクションを `DeadlockException`、
`TransactionException`（ロック待ちタイムアウト）、または — `IsolationLevel.Serializable` 下では —
`SerializabilityException` でアボートする。これらは *一時的* である: アボートされたトランザクションは
永続的な変更を何も行っていないため、小さな有界バックオフを挟んで **トランザクション全体** を
リトライすること（部分的にではなく）:

```csharp
T WithRetry<T>(Func<T> runTxn, int maxAttempts = 5)
{
    for (int attempt = 1; ; attempt++)
    {
        try { return runTxn(); }
        catch (Exception e) when (
            e is DeadlockException or TransactionException or SerializabilityException
            && attempt < maxAttempts)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(2 * attempt)); // back off, retry whole txn
        }
    }
}
```

### 書き込みの直列化（推奨） {#write-serialization}

サポートされる形は単一ライタであるため、すべての書き込みを 1 つのライタに集約すること。2 つのパターン:

1. **書き込みゲート** — すべての書き込みトランザクションを `SemaphoreSlim(1, 1)`（または `lock`）でガードする:

   ```csharp
   private static readonly SemaphoreSlim WriteGate = new(1, 1);

   async Task WriteAsync(Action<IGraphTransaction> work)
   {
       await WriteGate.WaitAsync();
       try
       {
           using var tx = db.BeginTransaction(); // begin + use + commit, all on this thread
           work(tx);
           tx.Commit();
       }
       finally { WriteGate.Release(); }
   }
   ```

2. **専用のライタスレッド** — 書き込みジョブをキュー（例: `System.Threading.Channels.Channel<T>`）に
   積み、1 つのバックグラウンドスレッドがそれを drain して、各トランザクションをそのスレッド上で
   開始・実行・commit する。これは自然なバッチングももたらす。

読み取りにゲートは不要: `BeginReadOnlyTransaction()` を任意のスレッドで開き、互いに、そしてライタと
並行して実行すること。

検証済みの並行ライタ（およびよりきめ細かいインデックスロック）のサポートは将来の課題である。

## BM25 コーパス統計はスナップショットベース {#bm25-stats}

`GraphStats` スナップショットが `Search` に与えられると、BM25 スコアリングはコーパス文書数 `N`、
平均文書長 `avgdl`、term ごとの `df` をそのスナップショットから取得し、クエリのたびに norms / postings を
再スキャンしない（FTS-4 最適化）。したがって、インデックス変更後に再利用されたスナップショットは近似的で
ある — `avgdl` / `df` がライブインデックスから遅延し、BM25 スコアをわずかにずらしうる。これは許容された
トレードオフである: BM25 はコーパス統計の陳腐化に頑健であり、`avgdl` はすべての文書の長さ正規化を
一様にスケールする。

これはスコアリングにのみ影響する。WAND top-k 枝刈りは陳腐化に依存しない term ごとの上限
(`idf * (K1 + 1)`) を用いるため、厳密な全スキャンと比べて文書を取りこぼすことは決してない。また両方の
クエリパスが同一のスナップショット基準を用いるため、互いに整合し続ける。

## HNSW の上書き {#hnsw-overwrite}

既存 sequence に対する `HnswIndex.Insert(seq)` は、ベクトル payload を更新するが HNSW グラフの
トポロジを再リンクしない。古いグラフリンクは新しいベクトルを指したまま残る。これはベクトルが大きく
変化したときに検索品質を低下させうる。

緩和策: tombstone 数がライブ数を超えたときの自動 rebuild。

## 自動マイグレーションなし {#no-migration}

異なる `FormatVersion` のデータベースを開くと `FormatVersionMismatchException` をスローする。
自動マイグレーションのパスは存在しない。データベースはソースデータから作り直す必要がある。

## In-Process のみ {#in-process}

Quiver はアプリケーションプロセス内で動作する。サーバモード・ネットワークプロトコル・プロセス間
アクセスは存在しない。`*.quiver` ファイルは排他ファイルロック (`FileShare.None`) で開かれる。

## チェックポイントによる WAL 切り詰め {#wal-truncation}

WAL の切り詰めは、完全なチェックポイント (Begin + End) の後にのみ発生する。アプリケーションが
チェックポイントなしで長時間動作する場合（例: 非常に大きく長時間実行されるトランザクション）、
WAL ファイルは際限なく増大する。
