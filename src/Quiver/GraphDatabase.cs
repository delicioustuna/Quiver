using Quiver.Logical;
using Quiver.Maintenance;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Microsoft.Extensions.Logging;

namespace Quiver;

/// <summary>
/// Quiver の最上位エントリポイント。グラフデータベースのオープン・トランザクション開始・
/// バルクロード・スナップショットビュー構築・統計収集の窓口を提供する。
/// </summary>
/// <remarks>
/// 内部では <see cref="IGraphStorageBackend"/> を介してストレージバックエンドを
/// 切り替え可能。通常は <see cref="Open"/> で生成し、用が済んだら
/// <see cref="Dispose"/> または <see cref="DisposeAsync"/> で破棄する。
/// </remarks>
public sealed class GraphDatabase : IDisposable, IAsyncDisposable
{
    private readonly IGraphStorageBackendInternal _backend;
    private readonly string _path;
    // AutoVacuum が有効なときのみ非 null。Dispose で停止する。
    private readonly AutoVacuumWorker? _autoVacuumWorker;
    private readonly bool _enforceExclusiveWriter;
    private readonly SemaphoreSlim _writerSemaphore = new(1, 1);

    private GraphDatabase(
        IGraphStorageBackend backend,
        string path,
        AutoVacuumWorker? autoVacuumWorker = null,
        bool enforceExclusiveWriter = false)
    {
        // 内部 SPI へキャスト。
        _backend = (IGraphStorageBackendInternal)backend;
        _path = path;
        _autoVacuumWorker = autoVacuumWorker;
        _enforceExclusiveWriter = enforceExclusiveWriter;
    }

    /// <summary><see cref="Open"/> に渡したデータベースファイルのパス (<c>*.quiver</c>)。</summary>
    public string Path => _path;

    /// <summary>
    /// 指定した単一データベースファイル (<c>*.quiver</c>) を開く (存在しない場合は新規作成)。
    /// Quiver の binary backend は全データ (コア / 索引 / 隣接 / token / epoch) を
    /// 単一の <c>*.quiver</c> ファイルに格納し、運用中のみサイドカー <c>*.quiver-wal</c> を伴う。
    /// <paramref name="options"/> 経由でバックエンド種別やバッファプールサイズなどを指定可。
    /// </summary>
    /// <param name="filePath">データベースファイル (<c>*.quiver</c>) のパス。</param>
    /// <param name="options">起動オプション。<c>null</c> の場合は既定値が使われる。</param>
    public static GraphDatabase Open(string filePath, GraphDatabaseOptions? options = null)
    {
        options ??= new GraphDatabaseOptions();
        // ホット path 各所が参照する構造化ログのファサードに ILoggerFactory を流し込む。
        // null のときはあえて触らない — 別 DB が事前に設定したロガーを取り消さないことで、
        // テスト並列実行時の汚染や、複数 DB を 1 プロセスで開く運用での意外な reset を避ける
        // (OTel ActivitySource / EventSource は構造上プロセス共有なので、最後勝ち回避はここだけ)。
        if (options.LoggerFactory != null)
            Quiver.Telemetry.QuiverLog.LoggerFactory = options.LoggerFactory;
        var factory = options.BackendFactory ?? CreateDefaultFactory(options.Backend);
        var backend = factory.Open(filePath, options);

        // AutoVacuum 有効時は周期ワーカーを起動する。各 tick は backend.Vacuum() を
        // 呼ぶだけで、アクティブ tx があれば vacuum 自身が Skipped で安全に no-op する。
        AutoVacuumWorker? worker = null;
        if (options.AutoVacuum && options.AutoVacuumInterval > TimeSpan.Zero)
            worker = new AutoVacuumWorker(() => backend.Vacuum(), options.AutoVacuumInterval);

        return new GraphDatabase(backend, filePath, worker, options.EnforceExclusiveWriter);
    }

    private static IGraphStorageBackendFactory CreateDefaultFactory(BackendKind kind) => kind switch
    {
        BackendKind.Binary => new BinaryGraphStorageBackendFactory(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知のバックエンド種別です。"),
    };

    /// <summary>
    /// 下層バックエンド内部 SPI。embedding adapter など内部経路専用で、公開 API ではない。
    /// </summary>
    internal IGraphStorageBackendInternal BackendInternal => _backend;

    /// <summary>
    /// バルクロード用ローダを開始する。
    /// </summary>
    /// <param name="buildAdjacencyIndex">
    /// <c>true</c> の場合、<see cref="BulkLoader.Commit"/> 時に隣接ブロックビュー (graph.quiver
    /// 内テナントに同居) も併せて構築し、その後の読み取り専用トランザクションから
    /// 隣接ブロックストアが利用可能になる。
    /// </param>
    public BulkLoader BeginBulkLoad(bool buildAdjacencyIndex = false)
    {
        var fn = _backend.BulkLoad.BeginBinaryBulkLoad
            ?? throw new NotSupportedException(
                "現在のバックエンドはバイナリバルクロードに対応していません。");
        return fn(buildAdjacencyIndex);
    }

    /// <summary>
    /// 1000 万エッジ以上の大規模インポート向けに <see cref="StreamingBulkLoader"/> を開く。
    /// 追記中はリレーションシップレコードを一時ファイルにストリーミングするため、ピークヒープは
    /// dense ポインタ配列 (~32 × maxRelId バイト) でバウンドされ、全リレーションシップ数には依存しない。
    /// <c>AppendRelationship</c> は <see cref="RelationshipId"/> が厳密に増加する順序で呼ぶ必要がある。
    /// 現在のバックエンドにストリーミングバルクロード経路が無い場合は <see cref="NotSupportedException"/> を投げる。
    /// </summary>
    public StreamingBulkLoader BeginStreamingBulkLoad(bool buildAdjacencyIndex = false)
    {
        var fn = _backend.BulkLoad.BeginStreamingBinaryBulkLoad
            ?? throw new NotSupportedException(
                "現在のバックエンドはストリーミングバイナリバルクロードに対応していません。");
        return fn(buildAdjacencyIndex);
    }

    /// <summary>新規グラフトランザクションを開始する。</summary>
    /// <param name="level">分離レベル (既定: スナップショット分離)。</param>
    public IGraphTransaction BeginTransaction(
        IsolationLevel level = IsolationLevel.SnapshotIsolation)
    {
        if (_enforceExclusiveWriter)
        {
            if (!_writerSemaphore.Wait(0))
                throw new InvalidOperationException(
                    "別の書き込みトランザクションがアクティブです。EnforceExclusiveWriter が有効な場合、同時に開ける書き込みトランザクションは 1 つだけです。");
            IGraphTransaction tx;
            try
            {
                tx = _backend.BeginGraphTransaction(level, readOnly: false);
                RegisterWriterRelease(tx);
                return tx;
            }
            catch
            {
                _writerSemaphore.Release();
                throw;
            }
        }
        return _backend.BeginGraphTransaction(level, readOnly: false);
    }

    /// <summary>
    /// 新規グラフトランザクションを開始する。排他ライタが使用中の場合は非同期に待機する。
    /// </summary>
    /// <param name="level">分離レベル。</param>
    /// <param name="cancellationToken">排他ライタの待機を取り消すトークン。</param>
    /// <remarks>
    /// <see cref="GraphDatabaseOptions.EnforceExclusiveWriter"/> が無効な場合は同期的に完了する。
    /// 取得後のトランザクション操作は、<see cref="IGraphTransaction.CommitAsync"/> 等の
    /// Quiver が提供する境界を除いて同期実行すること。
    /// </remarks>
    public ValueTask<IGraphTransaction> BeginTransactionAsync(
        IsolationLevel level = IsolationLevel.SnapshotIsolation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_enforceExclusiveWriter)
            return ValueTask.FromResult(_backend.BeginGraphTransaction(level, readOnly: false));
        return BeginExclusiveTransactionAsync(level, cancellationToken);
    }

    private async ValueTask<IGraphTransaction> BeginExclusiveTransactionAsync(
        IsolationLevel level,
        CancellationToken cancellationToken)
    {
        await _writerSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tx = _backend.BeginGraphTransaction(level, readOnly: false);
            RegisterWriterRelease(tx);
            return tx;
        }
        catch
        {
            _writerSemaphore.Release();
            throw;
        }
    }

    private void RegisterWriterRelease(IGraphTransaction tx)
    {
        Action release = () => _writerSemaphore.Release();
        tx.OnCommitted(release);
        tx.OnRolledBack(release);
    }

    /// <summary>
    /// 読み取り専用としてマークしたスナップショット分離トランザクションを開く。
    /// 読み取り専用トランザクションは <see cref="Quiver.Operators.ParallelBfsOperator"/> など
    /// 並列トラバーサル系オペレータと安全に組み合わせられる。
    /// </summary>
    public IGraphTransaction BeginReadOnlyTransaction()
        => _backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: true);

    /// <summary>読み取り専用のスナップショット分離トランザクションを開始する。</summary>
    /// <param name="cancellationToken">開始前のキャンセルを通知するトークン。</param>
    /// <remarks>現在の実装は同期的に完了する。</remarks>
    public ValueTask<IGraphTransaction> BeginReadOnlyTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BeginReadOnlyTransaction());
    }

    /// <summary>ラベル・プロパティキー・リレーションシップ型・インデックスのスキーマ API。</summary>
    public ISchemaApi Schema => _backend.Schema;

    /// <summary>統計取得・整合性検査などの診断 API。</summary>
    public IDiagnosticsApi Diagnostics => _backend.Diagnostics;

    /// <summary>
    /// バックエンドのベクトルストア。<c>CreateVectorIndex</c> や
    /// <c>SetVector</c> は直接ここから呼ぶ。問い合わせ側のアクセスは
    /// トラバーサルソースの <c>g.Knn(...)</c> 経由。
    /// </summary>
    public Core.IVectorStore Vectors => _backend.Vectors;

    /// <summary>
    /// 埋め込みパイプライン (<c>Quiver.Embedding</c>) が消費する
    /// <see cref="Core.IGraphEngine"/> ブリッジを生成する。グラフ読み取りと
    /// <paramref name="vectors"/> / <paramref name="catalog"/> を 1 つのエンジン契約に束ねる。
    /// </summary>
    public Core.IGraphEngine CreateEmbeddingEngine(Core.IVectorStore vectors, Core.IVectorCatalog catalog)
        => new GraphEngineAdapter(this, vectors, catalog);

    /// <summary>
    /// データベース全体をスキャンして新しい <see cref="GraphStats"/> スナップショットを返す。
    /// O(N + E) のコストがかかるため、通常は起動時やバルクロード後に 1 回だけ呼ぶ。
    /// </summary>
    public GraphStats CollectStats()
        => CollectStats(GraphStats.PowerNodeDegreeThreshold);

    /// <summary>
    /// パワーノード判定の degree しきい値を呼び出し側でオーバーライドできる版。
    /// テストや診断で既定値 (<see cref="GraphStats.PowerNodeDegreeThreshold"/>) が
    /// 大きすぎる場合に有用。
    /// </summary>
    public GraphStats CollectStats(int powerNodeThreshold)
    {
        using var tx = _backend.Transactions.Begin(IsolationLevel.SnapshotIsolation);
        return GraphStats.Collect(tx, powerNodeThreshold);
    }

    /// <summary>
    /// ノード毎の degree lookup における dense / sparse 切り替えしきい値を
    /// 呼び出し側で調整できるオーバーロード。詳細は <see cref="NodeDegreeLookup"/> 参照。
    /// </summary>
    public GraphStats CollectStats(int powerNodeThreshold, double denseThreshold)
    {
        using var tx = _backend.Transactions.Begin(IsolationLevel.SnapshotIsolation);
        return GraphStats.Collect(tx, powerNodeThreshold, denseThreshold);
    }

    /// <summary>
    /// <see cref="QueryOptimizer"/> は内部最適化機構のため internal。指定の統計
    /// (または新規収集したスナップショット) を背景に持つオプティマイザを生成する。
    /// </summary>
    internal QueryOptimizer CreateOptimizer(GraphStats? stats = null)
        => new(stats ?? CollectStats());

    /// <summary>
    /// PageRank・Louvain・繰り返し BFS / 最短経路など
    /// 同一グラフ状態を複数回パスするアルゴリズム向けに、現在のグラフのポイントインタイム CSR/CSC
    /// スナップショットを構築する。構築は O(N + E)。その後の隣接アクセスはフラット配列の参照に
    /// なるため、ノード毎に隣接カーソルを開くより安価になる。
    /// </summary>
    /// <remarks>
    /// 内部でスナップショット分離の読み取り専用トランザクションを開いてビューを構築し、
    /// その後トランザクションを閉じるので、返却ビューは構築後にトランザクションを留めない。
    /// 本呼び出し以降のミューテーションはスナップショットからは見えない。プール済み配列を
    /// 解放するためビューは <see cref="IDisposable.Dispose"/> で破棄すること。
    /// </remarks>
    public IGraphSnapshotView OpenSnapshotView()
    {
        using var tx = _backend.Transactions.Begin(IsolationLevel.SnapshotIsolation);
        return GraphSnapshotView.Build(tx.Nodes, tx.Relationships, tx.AdjacencyBlocks);
    }

    /// <summary>
    /// <see cref="RelationshipId"/> から
    /// <paramref name="propertyKey"/> のスカラ値へのジョインインデックス (SID 風) を構築する。
    /// 重み付きトラバーサル、エッジフィルタ、リレーション ID を既に持っているアルゴリズムカーネルで、
    /// プロパティチェーンを辿らずに値を取り出す用途を想定。
    /// </summary>
    /// <remarks>
    /// 構築コストは O(R + P) (リレーションストアの 1 パス + 各エッジのプロパティチェーン走査)。
    /// 構築後のミューテーションは可視化されない — 鮮度が必要なら、グラフを変更した後に再構築する。
    /// 返却インデックスは構築トランザクションよりも長く生存可能。
    /// </remarks>
    /// <param name="propertyKey">プロパティキー名。<see cref="ISchemaApi.GetOrCreatePropertyKey"/> で事前に作成済みであること。</param>
    /// <param name="expectedType">射影するスカラ型。他の型の値はスキップされる。</param>
    public Storage.Records.IRelationshipPropertyJoinIndex BuildRelationshipPropertyJoinIndex(
        string propertyKey,
        Storage.Records.PropertyValueType expectedType)
    {
        ArgumentNullException.ThrowIfNull(propertyKey);
        var keyId = _backend.Schema.GetOrCreatePropertyKey(propertyKey);
        using var tx = _backend.Transactions.Begin(IsolationLevel.SnapshotIsolation);
        return Storage.Records.DirectArrayRelationshipPropertyJoinIndex.Build(
            tx.Relationships, tx.Properties, keyId, expectedType);
    }

    // ===== opt-in 列指向 (CreateColumn / DropColumn) =====

    private BinaryGraphStorageBackend RequireBinaryForColumns()
        => _backend as BinaryGraphStorageBackend
           ?? throw new NotSupportedException("列指向 (CreateColumn) は binary backend 専用です。");

    /// <summary>
    /// 指定 <paramref name="kind"/> の scalar プロパティ <paramref name="propertyKey"/> を
    /// 列化登録する (opt-in)。現データから列を構築し登録を永続化する。既に列化済みなら false。
    /// <para>5b 時点では登録 + 初期構築まで。以後の write での自動維持は 5c で配線する。</para>
    /// </summary>
    public bool CreateColumn(Core.EntityKind kind, string propertyKey)
    {
        ArgumentNullException.ThrowIfNull(propertyKey);
        var keyId = _backend.Schema.GetOrCreatePropertyKey(propertyKey);
        return RequireBinaryForColumns().CreateColumn(kind, keyId.Value);
    }

    /// <summary>列化登録を解除する。未登録なら false。</summary>
    public bool DropColumn(Core.EntityKind kind, string propertyKey)
    {
        ArgumentNullException.ThrowIfNull(propertyKey);
        if (!_backend.Schema.TryGetPropertyKeyId(propertyKey, out var keyId)) return false;
        return RequireBinaryForColumns().DropColumn(kind, keyId.Value);
    }

    /// <summary>列指向の読み取り経路を検証するため、列の可視値合計を返す。未登録なら -1。</summary>
    internal long ColumnProjectSumForTest(Core.EntityKind kind, string propertyKey)
    {
        if (!_backend.Schema.TryGetPropertyKeyId(propertyKey, out var keyId)) return -1;
        return RequireBinaryForColumns().ColumnProjectSumForTest(kind, keyId.Value);
    }

    /// <summary>
    /// 現在のリレーションシップ状態から不変ベースビューを再構築し、
    /// tombstone を破棄してエポックを進める。本呼び出し以降、生存中のエッジはすべてベースビューから
    /// 提供され、新しいリレーションシップが作成されるまで delta ウォークは no-op になる。
    /// payload lane の無いバイナリバックエンド以外はサポート対象外で、その場合は例外を投げる。
    /// 呼び出し前にアクティブトランザクションが無いことを呼び出し側が保証すること。
    /// </summary>
    public void CompactAdjacency()
    {
        if (_backend is BinaryGraphStorageBackend binary)
            binary.CompactAdjacency();
        else
            throw new NotSupportedException(
                "CompactAdjacency はバイナリバックエンドのみ実装されています。");
    }

    /// <summary>
    /// 書き込みを止めずに <paramref name="targetFilePath"/> (<c>*.quiver</c>) へ
    /// ライブスナップショットを取る。target は <see cref="Open"/> で独立した DB として開ける。
    /// 内部では (1) ベストエフォートでシャープチェックポイントを起動、(2) 単一コンテナを
    /// page-by-page で複製、(3) WAL を末尾までフラッシュして単一サイドカーを複製、という流れで、
    /// 並行 writer はフレームレベルロックの粒度で短くしか待たない。target を開くと recovery が走り、
    /// snapshot 時点までの commit 群が redo され、中途半端だった in-flight tx は
    /// CompensationLogRecord で undo される。
    /// バイナリ以外のバックエンドはサポート対象外 (<see cref="NotSupportedException"/>)。
    /// </summary>
    public void CreateSnapshot(string targetFilePath, SnapshotOptions? options = null)
        => _backend.CreateSnapshot(targetFilePath, options);

    /// <summary>
    /// 削除済みエンティティ (MVCC の dead version) を物理回収する vacuum を
    /// 同期的に実行する。アクティブトランザクションがあるときは安全側で何もせず
    /// <see cref="VacuumReport.Skipped"/> = true で返る。
    /// 現状の MVP はノードストアのみを対象とする (リレーション / プロパティ / 索引の
    /// 物理回収は後続ステップで拡張)。<see cref="VacuumOptions.Mode"/> に
    /// <see cref="VacuumMode.DryRun"/> を渡せば書き込み無しで実行できる。
    /// バイナリ以外のバックエンドはサポート対象外 (<see cref="NotSupportedException"/>)。
    /// </summary>
    public VacuumReport Vacuum(VacuumOptions? options = null)
        => _backend.Vacuum(options);

    /// <summary>
    /// 与えたマイグレーションのうち未適用のものを <see cref="IMigration.Version"/> 昇順 →
    /// <see cref="IMigration.Id"/> Ordinal 昇順で適用する。各マイグレーションは独立した tx で実行され、
    /// 失敗時はその tx のミューテーションだけ rollback される (schema rename は tx 境界を跨ぐ点に注意)。
    /// 既に適用済みの ID は skip される (冪等)。
    /// </summary>
    public Task<Migrations.MigrationResult> MigrateAsync(
        IEnumerable<Migrations.IMigration> migrations,
        CancellationToken cancellationToken = default)
        => Migrations.Migrator.RunAsync(this, MigrationDirectory, migrations, cancellationToken);

    /// <summary>適用済みマイグレーション履歴のスナップショット (適用順)。</summary>
    public IReadOnlyList<Migrations.MigrationHistoryEntry> GetMigrationHistory()
        => new Migrations.MigrationHistory(MigrationDirectory).Entries;

    // 増分8: migrations.history はバックエンドのデータディレクトリに置く (operational metadata)。
    // backend の DataDirectory を正本とする。
    private string MigrationDirectory => _backend.DataDirectory;

    /// <summary>
    /// バックグラウンドの AutoVacuum ワーカーを停止してから下層バックエンドを破棄する。
    /// ワーカー停止は進行中の vacuum tick の完了を待ってから戻る。
    /// </summary>
    public void Dispose()
    {
        // 先にワーカーを止めてから backend を閉じる。逆順だと進行中 tick が
        // 破棄済み backend に触れて落ちうる。
        _autoVacuumWorker?.Dispose();
        _backend.Dispose();
    }

    /// <summary>データベースを非同期破棄する。現在の実装では同期破棄として完了する。</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// <see cref="GraphDatabase.Open"/> に渡す起動オプション。
/// バッファプール / WAL / ロックタイムアウト / チェックサム有効化 / バックエンド種別などを指定する。
/// </summary>
public sealed class GraphDatabaseOptions
{
    /// <summary>バッファプールの目標サイズ (バイト単位)。既定 256 MB。</summary>
    public long BufferPoolSize { get; set; } = 256 * 1024 * 1024;

    /// <summary>WAL 1 セグメントのサイズ (バイト単位)。既定 64 MB。</summary>
    public int WalSegmentSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// 案A: チェックポイント契機のしきい値 (バイト単位)。前回チェックポイント以降に
    /// WAL がこのバイト数以上成長し、かつアクティブトランザクションが 0 になった時点で、
    /// 全データページをフラッシュして WAL を truncate する。既定 64 MB。
    /// 0 以下を指定するとチェックポイントを行わず、WAL は単調増加する (旧挙動)。
    /// <see cref="CheckpointPolicy"/> が <see cref="Quiver.Transactions.CheckpointPolicy.Adaptive"/>
    /// のときは初期値としてのみ使われ、以降 <see cref="AdaptiveCheckpointController"/> が
    /// 観測した bytes/tx から自動再計算する。
    /// </summary>
    public long CheckpointThresholdBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// チェックポイント threshold の運用ポリシー。
    /// <see cref="Quiver.Transactions.CheckpointPolicy.Fixed"/> (既定) は
    /// <see cref="CheckpointThresholdBytes"/> をそのまま使い続ける旧挙動。
    /// <see cref="Quiver.Transactions.CheckpointPolicy.Adaptive"/> は直近
    /// <see cref="AdaptiveSampleWindow"/> 件の bytes/tx 移動平均から、
    /// <see cref="TargetRecoveryTime"/> を満たす threshold を周期的に再計算する。
    /// </summary>
    public Quiver.Transactions.CheckpointPolicy CheckpointPolicy { get; set; }
        = Quiver.Transactions.CheckpointPolicy.Fixed;

    /// <summary>
    /// <see cref="Quiver.Transactions.CheckpointPolicy.Adaptive"/> 選択時の
    /// 復旧時間目標。recovery が WAL を再生する際の上限値として扱い、threshold が
    /// この目標を超えないように制御する。既定 5 秒。
    /// </summary>
    public TimeSpan TargetRecoveryTime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Adaptive 計算時の threshold 下限 (バイト単位)。これより小さい threshold は
    /// 採用しない。書き込みの度に checkpoint が走る病的な状態を避けるための安全弁。既定 4 MB。
    /// </summary>
    public long MinCheckpointThresholdBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>
    /// Adaptive 計算時の threshold 上限 (バイト単位)。これより大きい threshold は
    /// 採用しない。WAL が過大に肥大するのを避けるための上限。既定 1 GB。
    /// </summary>
    public long MaxCheckpointThresholdBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Adaptive 移動平均のサンプル窓 (トランザクション数)。リングバッファで
    /// 直近 N 件の bytes/tx を保持する。既定 1000。
    /// </summary>
    public int AdaptiveSampleWindow { get; set; } = 1000;

    /// <summary>ロック取得のタイムアウト。既定 5 秒。</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// ロック戦略。<see cref="Quiver.Transactions.LockingMode.ExclusiveOnly"/> (既定) は
    /// 読み取りロック無し (現挙動)、<see cref="Quiver.Transactions.LockingMode.ReaderWriter"/> は
    /// 読み取りを <see cref="Quiver.Transactions.LockMode.Shared"/>・書き込みを
    /// <see cref="Quiver.Transactions.LockMode.Exclusive"/> として、複数 reader 間の競合を解消する。
    /// </summary>
    public Quiver.Transactions.LockingMode LockingMode { get; set; }
        = Quiver.Transactions.LockingMode.ExclusiveOnly;

    /// <summary>ページのチェックサム計算 / 検証を有効にするか。既定 <c>true</c>。</summary>
    public bool EnableChecksums { get; set; } = true;

    /// <summary>ロギング用 <see cref="ILoggerFactory"/>。null のときはログ無し。</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>
    /// <see cref="BackendFactory"/> が null のときに利用する組み込みバックエンドの種別。
    /// 既定は <see cref="BackendKind.Binary"/>。
    /// </summary>
    public BackendKind Backend { get; set; } = BackendKind.Binary;

    /// <summary>
    /// 任意のファクトリ。設定すると <see cref="Backend"/> をオーバーライドする。
    /// テストからインメモリバックエンド等を注入する用途を想定。
    /// </summary>
    public IGraphStorageBackendFactory? BackendFactory { get; set; }

    /// <summary>
    /// null でない場合、書き込みトランザクションが
    /// 公開するすべてのグラフミューテーション (<c>CreateNode</c>、<c>CreateRelationship</c>、
    /// <c>SetProperty</c> など) をバックエンドが記録し、コミットが永続化された後に
    /// このシンクへバッチで引き渡す。バイナリバックエンドはクラッシュリカバリ向けに
    /// PageImage WAL を引き続き利用する — 論理ストリームはオプションの 2 系統目で、
    /// デバッグ / 監査ログ転送・マイグレーション・将来の
    /// レプリケーション用途を想定。ロールバックされたトランザクションは届かない。
    /// </summary>
    public ILogicalMutationSink? LogicalMutationSink { get; set; }

    /// <summary>
    /// <c>true</c> のとき、バックエンド open 完了直後に
    /// <see cref="IDiagnosticsApi.RepairIndexes"/> を <see cref="IndexRepairMode.Apply"/> で
    /// 自動実行し、recovery 後に残った orphan 索引エントリを除去する。
    /// 既定 <c>false</c> (運用者が必要なときに <see cref="IDiagnosticsApi.CheckIndexConsistency"/> /
    /// <see cref="IDiagnosticsApi.RepairIndexes"/> を明示的に呼ぶ前提)。
    /// </summary>
    public bool AutoRepairOrphansOnRecovery { get; set; } = false;

    /// <summary>
    /// デッドロック検出器の周期。<c>null</c> または <see cref="TimeSpan.Zero"/> 以下で無効化
    /// (既定。<see cref="LockTimeout"/> でフォールバックする旧挙動)。値を設定すると周期ごとに
    /// 全 <c>LockManager</c> の wait-for graph snapshot を取り、Tarjan SCC で閉路を検出する。
    /// 閉路内で最も若い tx (<see cref="Quiver.Core.TransactionId.Value"/> が最大) を犠牲者として
    /// <see cref="Quiver.Transactions.DeadlockException"/> で中断させる。
    /// 推奨値: 100ms (検出遅延が短く、CPU オーバーヘッドも 1% 未満を狙える)。
    /// </summary>
    public TimeSpan? DeadlockDetectionInterval { get; set; }

    /// <summary>
    /// WAL グループコミットの coalesce window。<see cref="TimeSpan.Zero"/> (既定) で無効
    /// (各 commit の <c>FlushTo</c> が即座に fsync を起動する旧挙動)。0 より大きい値を指定すると、
    /// 最初の commit が到着した時点でこの window の経過まで spin-wait して後続 commit を貯め、
    /// 累積した全 commit を 1 回の fsync で一括処理する。
    /// 効果: 多 commit 並列ワークロードでは fsync 回数が激減し IOPS を節約できる。代償として
    /// 単一 commit のレイテンシが (fsync 自体の時間 + window) まで増える。推奨値は 100µs
    /// 〜 1ms。Windows の <c>Task.Delay</c> 解像度 (~15ms) を回避するため、内部実装は
    /// <see cref="System.Diagnostics.Stopwatch"/> + <see cref="Thread.SpinWait"/> による
    /// busy-wait で sub-millisecond 精度を確保している (専用 LongRunning スレッドで実行されるため
    /// 他スレッドを阻害しない)。
    /// </summary>
    public TimeSpan GroupCommitWindow { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// <c>true</c> のとき、<see cref="GraphDatabase.BeginTransaction"/> で既にアクティブな
    /// 書き込みトランザクションが存在する場合に <see cref="InvalidOperationException"/> をスローする。
    /// 既定 <c>false</c> (複数 writer を許容する既存挙動)。
    /// </summary>
    public bool EnforceExclusiveWriter { get; set; }

    /// <summary>
    /// <c>true</c> のとき、バックエンドが提供するバックグラウンドワーカーで
    /// 周期的に <see cref="GraphDatabase.Vacuum"/> を起動する。既定 <c>false</c>
    /// (運用者が明示的に <see cref="GraphDatabase.Vacuum"/> を呼ぶ前提)。
    /// <c>true</c> かつ
    /// <see cref="AutoVacuumInterval"/> が正のとき、<see cref="GraphDatabase.Open"/> が
    /// <see cref="Quiver.Maintenance.AutoVacuumWorker"/> を起動し、
    /// <see cref="GraphDatabase.Dispose"/> で停止する。
    /// </summary>
    public bool AutoVacuum { get; set; } = false;

    /// <summary>
    /// <see cref="AutoVacuum"/> 有効時の vacuum 起動周期。既定 1 時間。
    /// 初回も DB open から 1 周期後に発火する (open 直後の vacuum 突入で起動レイテンシを
    /// 悪化させないため)。<see cref="TimeSpan.Zero"/> 以下にすると <see cref="AutoVacuum"/> が
    /// <c>true</c> でもワーカーは起動しない。
    /// </summary>
    public TimeSpan AutoVacuumInterval { get; set; } = TimeSpan.FromHours(1);
}
