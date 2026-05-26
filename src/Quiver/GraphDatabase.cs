using Quiver.Logical;
using Quiver.Maintenance;
using Quiver.Stores;
using Quiver.Transactions;
using Microsoft.Extensions.Logging;

namespace Quiver;

/// <summary>
/// Quiver の最上位エントリポイント。グラフデータベースのオープン・トランザクション開始・
/// バルクロード・スナップショットビュー構築・統計収集の窓口を提供する。
/// </summary>
/// <remarks>
/// 内部では <see cref="IGraphStorageBackend"/> を介してバイナリ / SQLite など複数の
/// ストレージバックエンドを切り替え可能。通常は <see cref="Open"/> で生成し、用が済んだら
/// <see cref="Dispose"/> で破棄する。
/// </remarks>
public sealed class GraphDatabase : IDisposable
{
    private readonly IGraphStorageBackend _backend;

    private GraphDatabase(IGraphStorageBackend backend)
    {
        _backend = backend;
    }

    /// <summary>
    /// 指定ディレクトリのデータベースを開く (存在しない場合は新規作成)。
    /// <paramref name="options"/> 経由でバックエンド種別やバッファプールサイズなどを指定可。
    /// </summary>
    /// <param name="directoryPath">データベースディレクトリのパス。</param>
    /// <param name="options">起動オプション。<c>null</c> の場合は既定値が使われる。</param>
    public static GraphDatabase Open(string directoryPath, GraphDatabaseOptions? options = null)
    {
        options ??= new GraphDatabaseOptions();
        // OB-3: ホット path 各所が参照する構造化ログのファサードに ILoggerFactory を流し込む。
        // null のときはあえて触らない — 別 DB が事前に設定したロガーを取り消さないことで、
        // テスト並列実行時の汚染や、複数 DB を 1 プロセスで開く運用での意外な reset を避ける
        // (OTel ActivitySource / EventSource は構造上プロセス共有なので、最後勝ち回避はここだけ)。
        if (options.LoggerFactory != null)
            Quiver.Core.Telemetry.QuiverLog.LoggerFactory = options.LoggerFactory;
        var factory = options.BackendFactory ?? CreateDefaultFactory(options.Backend);
        var backend = factory.Open(directoryPath, options);
        return new GraphDatabase(backend);
    }

    private static IGraphStorageBackendFactory CreateDefaultFactory(BackendKind kind) => kind switch
    {
        BackendKind.Binary => new BinaryGraphStorageBackendFactory(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知のバックエンド種別です。"),
    };

    /// <summary>
    /// 下層バックエンドを公開する。診断やバックエンド固有機能の利用が目的で、
    /// 通常の呼び出しは <see cref="BeginTransaction"/> 等を優先する。
    /// </summary>
    public IGraphStorageBackend Backend => _backend;

    /// <summary>
    /// バルクロード用ローダを開始する。
    /// </summary>
    /// <param name="buildAdjacencyIndex">
    /// <c>true</c> の場合、<see cref="BulkLoader.Commit"/> 時に adj.db + adj_idx.dat も
    /// 同時に構築し、その後の読み取り専用トランザクションから隣接ブロックストアが利用可能になる。
    /// </param>
    public BulkLoader BeginBulkLoad(bool buildAdjacencyIndex = false)
    {
        var fn = _backend.BulkLoad.BeginBinaryBulkLoad
            ?? throw new NotSupportedException(
                "現在のバックエンドはバイナリバルクロードに対応していません。");
        return fn(buildAdjacencyIndex);
    }

    /// <summary>
    /// PW-9: 1000 万エッジ以上の大規模インポート向けに <see cref="StreamingBulkLoader"/> を開く。
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
        => _backend.BeginGraphTransaction(level, readOnly: false);

    /// <summary>
    /// 読み取り専用としてマークしたスナップショット分離トランザクションを開く。
    /// 読み取り専用トランザクションは <see cref="Quiver.Operators.ParallelBfsOperator"/> など
    /// 並列トラバーサル系オペレータと安全に組み合わせられる。
    /// </summary>
    public IGraphTransaction BeginReadOnlyTransaction()
        => _backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: true);

    /// <summary>ラベル・プロパティキー・リレーションシップ型・インデックスのスキーマ API。</summary>
    public ISchemaApi Schema => _backend.Schema;

    /// <summary>統計取得・整合性検査などの診断 API。</summary>
    public IDiagnosticsApi Diagnostics => _backend.Diagnostics;

    /// <summary>
    /// VEC-5: バックエンドのベクトルストア。<c>CreateVectorIndex</c> や
    /// <c>SetVector</c> は直接ここから呼ぶ。問い合わせ側のアクセスは
    /// トラバーサルソースの <c>g.Knn(...)</c> 経由。
    /// </summary>
    public Core.IVectorStore Vectors => _backend.Vectors;

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
    /// PW-16: ノード毎の degree lookup における dense / sparse 切り替えしきい値を
    /// 呼び出し側で調整できるオーバーロード。詳細は <see cref="NodeDegreeLookup"/> 参照。
    /// </summary>
    public GraphStats CollectStats(int powerNodeThreshold, double denseThreshold)
    {
        using var tx = _backend.Transactions.Begin(IsolationLevel.SnapshotIsolation);
        return GraphStats.Collect(tx, powerNodeThreshold, denseThreshold);
    }

    /// <summary>
    /// 指定の統計 (または新規収集したスナップショット) を背景に持つ <see cref="QueryOptimizer"/> を生成する。
    /// </summary>
    public QueryOptimizer CreateOptimizer(GraphStats? stats = null)
        => new(stats ?? CollectStats());

    /// <summary>
    /// PW-15 / codex_advice_3 7.7 節。PageRank・Louvain・繰り返し BFS / 最短経路など
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
    /// FT-12 / codex_advice_3 7.3 節。<see cref="RelationshipId"/> から
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
    public Stores.IRelationshipPropertyJoinIndex BuildRelationshipPropertyJoinIndex(
        string propertyKey,
        Stores.PropertyValueType expectedType)
    {
        ArgumentNullException.ThrowIfNull(propertyKey);
        var keyId = _backend.Schema.GetOrCreatePropertyKey(propertyKey);
        using var tx = _backend.Transactions.Begin(IsolationLevel.SnapshotIsolation);
        return Stores.DirectArrayRelationshipPropertyJoinIndex.Build(
            tx.Relationships, tx.Properties, keyId, expectedType);
    }

    /// <summary>
    /// PW-14 / codex_advice_3 7.6 節。現在のリレーションシップ状態から不変ベースビューを再構築し、
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
    /// OP-1: 書き込みを止めずに <paramref name="targetDirectory"/> にライブスナップショットを
    /// 取る。target は <see cref="Open"/> で独立した DB として開ける。
    ///
    /// 内部では (1) ベストエフォートでシャープチェックポイントを起動、(2) page-by-page で
    /// データ / 索引ファイルを複製、(3) WAL を末尾までフラッシュしてセグメントを複製、
    /// という流れで、並行 writer はフレームレベルロックの粒度で短くしか待たない。
    /// target を開くと recovery が走り、snapshot 時点までの commit 群が redo され、
    /// 中途半端だった in-flight tx は CompensationLogRecord で undo される。
    ///
    /// バイナリ以外のバックエンドはサポート対象外 (<see cref="NotSupportedException"/>)。
    /// </summary>
    public void CreateSnapshot(string targetDirectory, SnapshotOptions? options = null)
        => _backend.CreateSnapshot(targetDirectory, options);

    /// <summary>
    /// OP-3: 削除済みエンティティ (FT-26 MVCC の dead version) を物理回収する vacuum を
    /// 同期的に実行する。アクティブトランザクションがあるときは安全側で何もせず
    /// <see cref="VacuumReport.Skipped"/> = true で返る。
    ///
    /// 現状の MVP はノードストアのみを対象とする (リレーション / プロパティ / 索引の
    /// 物理回収は後続ステップで拡張)。<see cref="VacuumOptions.Mode"/> に
    /// <see cref="VacuumMode.DryRun"/> を渡せば書き込み無しで実行できる。
    ///
    /// バイナリ以外のバックエンドはサポート対象外 (<see cref="NotSupportedException"/>)。
    /// </summary>
    public VacuumReport Vacuum(VacuumOptions? options = null)
        => _backend.Vacuum(options);

    /// <summary>下層バックエンドを破棄する。</summary>
    public void Dispose() => _backend.Dispose();
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
    /// FT-28: チェックポイント threshold の運用ポリシー。
    /// <see cref="Quiver.Transactions.CheckpointPolicy.Fixed"/> (既定) は
    /// <see cref="CheckpointThresholdBytes"/> をそのまま使い続ける旧挙動。
    /// <see cref="Quiver.Transactions.CheckpointPolicy.Adaptive"/> は直近
    /// <see cref="AdaptiveSampleWindow"/> 件の bytes/tx 移動平均から、
    /// <see cref="TargetRecoveryTime"/> を満たす threshold を周期的に再計算する。
    /// </summary>
    public Quiver.Transactions.CheckpointPolicy CheckpointPolicy { get; set; }
        = Quiver.Transactions.CheckpointPolicy.Fixed;

    /// <summary>
    /// FT-28: <see cref="Quiver.Transactions.CheckpointPolicy.Adaptive"/> 選択時の
    /// 復旧時間目標。recovery が WAL を再生する際の上限値として扱い、threshold が
    /// この目標を超えないように制御する。既定 5 秒。
    /// </summary>
    public TimeSpan TargetRecoveryTime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// FT-28: Adaptive 計算時の threshold 下限 (バイト単位)。これより小さい threshold は
    /// 採用しない。書き込みの度に checkpoint が走る病的な状態を避けるための安全弁。既定 4 MB。
    /// </summary>
    public long MinCheckpointThresholdBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>
    /// FT-28: Adaptive 計算時の threshold 上限 (バイト単位)。これより大きい threshold は
    /// 採用しない。WAL が過大に肥大するのを避けるための上限。既定 1 GB。
    /// </summary>
    public long MaxCheckpointThresholdBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// FT-28: Adaptive 移動平均のサンプル窓 (トランザクション数)。リングバッファで
    /// 直近 N 件の bytes/tx を保持する。既定 1000。
    /// </summary>
    public int AdaptiveSampleWindow { get; set; } = 1000;

    /// <summary>ロック取得のタイムアウト。既定 5 秒。</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// FT-24: ロック戦略。<see cref="Quiver.Transactions.LockingMode.ExclusiveOnly"/> (既定) は
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
    /// BA-7 / codex_advice_3 8 節。null でない場合、書き込みトランザクションが
    /// 公開するすべてのグラフミューテーション (<c>CreateNode</c>、<c>CreateRelationship</c>、
    /// <c>SetProperty</c> など) をバックエンドが記録し、コミットが永続化された後に
    /// このシンクへバッチで引き渡す。バイナリバックエンドはクラッシュリカバリ向けに
    /// PageImage WAL を引き続き利用する — 論理ストリームはオプションの 2 系統目で、
    /// SQLite バックエンド連携・デバッグ / 監査ログ転送・マイグレーション・将来の
    /// レプリケーション用途を想定。ロールバックされたトランザクションは届かない。
    /// </summary>
    public ILogicalMutationSink? LogicalMutationSink { get; set; }

    /// <summary>
    /// FT-22: <c>true</c> のとき、バックエンド open 完了直後に
    /// <see cref="IDiagnosticsApi.RepairIndexes"/> を <see cref="IndexRepairMode.Apply"/> で
    /// 自動実行し、recovery 後に残った orphan 索引エントリを除去する。
    /// 既定 <c>false</c> (運用者が必要なときに <see cref="IDiagnosticsApi.CheckIndexConsistency"/> /
    /// <see cref="IDiagnosticsApi.RepairIndexes"/> を明示的に呼ぶ前提)。
    /// </summary>
    public bool AutoRepairOrphansOnRecovery { get; set; } = false;

    /// <summary>
    /// FT-25: デッドロック検出器の周期。<c>null</c> または <see cref="TimeSpan.Zero"/> 以下で無効化
    /// (既定。<see cref="LockTimeout"/> でフォールバックする旧挙動)。値を設定すると周期ごとに
    /// 全 <c>LockManager</c> の wait-for graph snapshot を取り、Tarjan SCC で閉路を検出する。
    /// 閉路内で最も若い tx (<see cref="Quiver.Core.TransactionId.Value"/> が最大) を犠牲者として
    /// <see cref="Quiver.Transactions.DeadlockException"/> で中断させる。
    /// 推奨値: 100ms (検出遅延が短く、CPU オーバーヘッドも 1% 未満を狙える)。
    /// </summary>
    public TimeSpan? DeadlockDetectionInterval { get; set; }

    /// <summary>
    /// FT-27: WAL グループコミットの coalesce window。<see cref="TimeSpan.Zero"/> (既定) で無効
    /// (各 commit の <c>FlushTo</c> が即座に fsync を起動する旧挙動)。0 より大きい値を指定すると、
    /// 最初の commit が到着した時点でこの window の経過まで spin-wait して後続 commit を貯め、
    /// 累積した全 commit を 1 回の fsync で一括処理する。
    ///
    /// 効果: 多 commit 並列ワークロードでは fsync 回数が激減し IOPS を節約できる。代償として
    /// 単一 commit のレイテンシが (fsync 自体の時間 + window) まで増える。推奨値は 100µs
    /// 〜 1ms。Windows の <c>Task.Delay</c> 解像度 (~15ms) を回避するため、内部実装は
    /// <see cref="System.Diagnostics.Stopwatch"/> + <see cref="Thread.SpinWait"/> による
    /// busy-wait で sub-millisecond 精度を確保している (専用 LongRunning スレッドで実行されるため
    /// 他スレッドを阻害しない)。
    /// </summary>
    public TimeSpan GroupCommitWindow { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// OP-3: <c>true</c> のとき、バックエンドが提供するバックグラウンドワーカーで
    /// 周期的に <see cref="GraphDatabase.Vacuum"/> を起動する。既定 <c>false</c>
    /// (運用者が明示的に <see cref="GraphDatabase.Vacuum"/> を呼ぶ前提)。
    /// MVP では本フラグは設定値として保持されるのみで、自動起動経路は未実装。
    /// </summary>
    public bool AutoVacuum { get; set; } = false;
}
