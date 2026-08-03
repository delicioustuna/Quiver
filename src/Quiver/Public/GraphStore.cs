using Quiver.Core;
using Quiver.Maintenance;
using Quiver.Transactions;

namespace Quiver;

/// <summary>同期 callback 内で失敗した graph operation の診断情報。</summary>
/// <param name="Operation">失敗した操作名。</param>
/// <param name="Phase">失敗した処理フェーズ。</param>
/// <param name="ExceptionType">元の例外型の完全名。</param>
/// <param name="Trace">例外と stack trace。</param>
public sealed record GraphDiagnostic(
    string Operation,
    string Phase,
    string ExceptionType,
    string Trace);

/// <summary>graph callback の失敗と診断情報をまとめる例外。</summary>
public sealed class GraphOperationException : QuiverException
{
    internal GraphOperationException(GraphDiagnostic diagnostic, Exception inner)
        : base($"Graph operation '{diagnostic.Operation}' failed during '{diagnostic.Phase}'.", inner)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>失敗を再現するための診断情報。</summary>
    public GraphDiagnostic Diagnostic { get; }
}

/// <summary>同じ <see cref="GraphStore"/> の callback から callback へ再入した場合の例外。</summary>
public sealed class GraphCallbackReentrancyException : InvalidOperationException
{
    internal GraphCallbackReentrancyException()
        : base("同じ GraphStore の callback へ再入することはできません。")
    {
    }
}

/// <summary>writer lease が使用中だった場合の動作。</summary>
public enum GraphWriterContentionMode
{
    /// <summary>設定した timeout まで待機する。</summary>
    Wait,

    /// <summary>待機せず例外を送出する。</summary>
    FailFast,
}

/// <summary>checkpoint のしきい値制御方式。</summary>
public enum GraphCheckpointPolicy
{
    /// <summary>固定しきい値を使用する。</summary>
    Fixed,

    /// <summary>観測した WAL 増加量からしきい値を調整する。</summary>
    Adaptive,
}

/// <summary><see cref="GraphStore"/> の起動オプション。</summary>
public sealed class GraphStoreOptions
{
    /// <summary>新規ファイルの初期物理確保量。既定は 1 MiB。</summary>
    public long InitialFileAllocationBytes { get; set; } = 1024L * 1024L;

    /// <summary>一回のファイル拡張量の上限。既定は 64 MiB。</summary>
    public long MaximumFileGrowthStepBytes { get; set; } = 64L * 1024L * 1024L;

    /// <summary>buffer pool の目標サイズ。既定は 256 MiB。</summary>
    public long BufferPoolSize { get; set; } = 256L * 1024L * 1024L;

    /// <summary>checkpoint を要求する WAL バイト数。</summary>
    public long CheckpointThresholdBytes { get; set; } = 64L * 1024L * 1024L;

    /// <summary>checkpoint のしきい値制御方式。</summary>
    public GraphCheckpointPolicy CheckpointPolicy { get; set; } = GraphCheckpointPolicy.Fixed;

    /// <summary>adaptive checkpoint が目標とする復旧時間。</summary>
    public TimeSpan TargetRecoveryTime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>adaptive checkpoint しきい値の下限。</summary>
    public long MinimumCheckpointThresholdBytes { get; set; } = 4L * 1024L * 1024L;

    /// <summary>adaptive checkpoint しきい値の上限。</summary>
    public long MaximumCheckpointThresholdBytes { get; set; } = 1024L * 1024L * 1024L;

    /// <summary>adaptive checkpoint の移動平均に使う transaction 数。</summary>
    public int AdaptiveSampleWindow { get; set; } = 1000;

    /// <summary>writer lease を待つ最大時間。</summary>
    public TimeSpan WriterWaitTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>writer lease が使用中だった場合の動作。</summary>
    public GraphWriterContentionMode WriterContentionMode { get; set; } = GraphWriterContentionMode.Wait;

    /// <summary>ページ checksum を有効にするか。</summary>
    public bool EnableChecksums { get; set; } = true;

    /// <summary>recovery 後に orphan を自動修復するか。</summary>
    public bool AutoRepairOrphansOnRecovery { get; set; }

    /// <summary>定期 vacuum を有効にするか。</summary>
    public bool AutoVacuum { get; set; }

    /// <summary>自動 vacuum の間隔。</summary>
    public TimeSpan AutoVacuumInterval { get; set; } = TimeSpan.FromHours(1);

    internal QuiverDatabaseOptions ToCore(bool inMemory)
    {
        return new QuiverDatabaseOptions
        {
            InitialFileAllocationBytes = InitialFileAllocationBytes,
            MaximumFileGrowthStepBytes = MaximumFileGrowthStepBytes,
            BufferPoolSize = BufferPoolSize,
            CheckpointThresholdBytes = CheckpointThresholdBytes,
            CheckpointPolicy = CheckpointPolicy == GraphCheckpointPolicy.Adaptive
                ? Transactions.CheckpointPolicy.Adaptive
                : Transactions.CheckpointPolicy.Fixed,
            TargetRecoveryTime = TargetRecoveryTime,
            MinCheckpointThresholdBytes = MinimumCheckpointThresholdBytes,
            MaxCheckpointThresholdBytes = MaximumCheckpointThresholdBytes,
            AdaptiveSampleWindow = AdaptiveSampleWindow,
            WriterWaitTimeout = WriterWaitTimeout,
            WriterContentionMode = WriterContentionMode == GraphWriterContentionMode.FailFast
                ? global::Quiver.WriterContentionMode.FailFast
                : global::Quiver.WriterContentionMode.Wait,
            EnableChecksums = EnableChecksums,
            AutoRepairOrphansOnRecovery = AutoRepairOrphansOnRecovery,
            AutoVacuum = AutoVacuum,
            AutoVacuumInterval = AutoVacuumInterval,
            Backend = inMemory ? BackendKind.InMemory : BackendKind.Binary,
        };
    }
}

/// <summary>長時間 snapshot、明示 transaction、高度操作の入口。</summary>
public sealed class AdvancedGraphStore
{
    private readonly QuiverDatabase _database;

    internal AdvancedGraphStore(QuiverDatabase database) => _database = database;

    /// <summary>開始時点の snapshot に束縛された読み取り session を開く。</summary>
    public GraphReadSession BeginRead() => new(_database.BeginReadTransaction());

    /// <summary>明示 commit または rollback を必要とする書き込み session を開く。</summary>
    public GraphWriteSession BeginWrite() => new(_database.BeginWriteTransaction());

    /// <summary>現在のデータベース統計を返す。</summary>
    public DatabaseStatistics GetStatistics() => _database.Diagnostics.GetStatistics();

    /// <summary>物理構造の整合性を読み取り専用で検査する。</summary>
    public ConsistencyReport CheckConsistency() => _database.Diagnostics.CheckConsistency();

    /// <summary>開いているデータベースのライブスナップショットを作成する。</summary>
    public void CreateSnapshot(string targetFilePath, SnapshotOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetFilePath);
        _database.CreateSnapshot(targetFilePath, options);
    }

    /// <summary>reader horizon より古い削除済み版を物理回収する。</summary>
    public VacuumReport Vacuum(VacuumOptions? options = null) => _database.Vacuum(options);

    /// <summary>現在の Edge から不変 adjacency view を再構築する。</summary>
    public void CompactAdjacency() => _database.CompactAdjacency();
}

/// <summary>
/// Quiver の既定エントリポイント。通常操作を同期 callback 内へ閉じ込め、
/// 長時間操作は <see cref="Advanced"/> から明示 session として提供する。
/// </summary>
public sealed class GraphStore : IDisposable
{
    private readonly QuiverDatabase _database;
    private readonly AsyncLocal<int> _callbackDepth = new();
    private readonly AsyncLocal<GraphDiagnostic?> _lastDiagnostic = new();
    private bool _disposed;

    private GraphStore(QuiverDatabase database)
    {
        _database = database;
        Advanced = new AdvancedGraphStore(database);
    }

    /// <summary>単一の <c>*.quiver</c> ファイルを開く。存在しない場合は作成する。</summary>
    public static GraphStore Open(string filePath, GraphStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        options ??= new GraphStoreOptions();
        return new GraphStore(QuiverDatabase.Open(filePath, options.ToCore(inMemory: false)));
    }

    /// <summary>プロセス内 RAM だけを使用する一時 store を作成する。</summary>
    public static GraphStore OpenMemory(GraphStoreOptions? options = null)
    {
        options ??= new GraphStoreOptions();
        return new GraphStore(QuiverDatabase.CreateInMemory(options.ToCore(inMemory: true)));
    }

    /// <summary>store のファイルパス。memory store では <c>:memory:</c>。</summary>
    public string Path => _database.Path;

    /// <summary>長時間 snapshot、明示 transaction、高度操作の入口。</summary>
    public AdvancedGraphStore Advanced { get; }

    /// <summary>現在の非同期コンテキストで最後に失敗した callback の診断情報。</summary>
    public GraphDiagnostic? LastDiagnostic => _lastDiagnostic.Value;

    /// <summary>読み取り callback を開始時点の snapshot で実行する。</summary>
    public void Read(GraphReadAction operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Read(scope =>
        {
            operation(scope);
            return true;
        });
    }

    /// <summary>読み取り callback を開始時点の snapshot で実行して結果を返す。</summary>
    public TResult Read<TResult>(GraphReadOperation<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        EnterCallback();
        IReadTransaction? transaction = null;
        GraphReadScope? scope = null;
        string phase = "begin";
        try
        {
            transaction = _database.BeginReadTransaction();
            scope = new GraphReadScope(transaction);
            phase = "callback";
            TResult result = operation(scope);
            RejectAsyncResult(result);
            return result;
        }
        catch (Exception exception) when (exception is not GraphOperationException)
        {
            throw CreateFailure("read", phase, exception);
        }
        finally
        {
            scope?.Deactivate();
            transaction?.Dispose();
            ExitCallback();
        }
    }

    /// <summary>書き込み callback を実行し、正常終了時だけ commit する。</summary>
    public void Write(GraphWriteAction operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Write(scope =>
        {
            operation(scope);
            return true;
        });
    }

    /// <summary>書き込み callback を実行し、正常終了時だけ commit して結果を返す。</summary>
    public TResult Write<TResult>(GraphWriteOperation<TResult> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        EnterCallback();
        IWriteTransaction? transaction = null;
        GraphWriteScope? scope = null;
        string phase = "begin";
        try
        {
            transaction = _database.BeginWriteTransaction();
            scope = new GraphWriteScope(transaction);
            phase = "callback";
            TResult result = operation(scope);
            RejectAsyncResult(result);
            phase = "commit";
            transaction.Commit();
            return result;
        }
        catch (Exception exception) when (exception is not GraphOperationException)
        {
            if (transaction?.State == TransactionState.Active)
                transaction.Rollback();
            throw CreateFailure("write", phase, exception);
        }
        finally
        {
            scope?.Deactivate();
            transaction?.Dispose();
            ExitCallback();
        }
    }

    /// <summary>store と下層 resource を閉じる。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _database.Dispose();
    }

    internal QuiverDatabase DatabaseInternal => _database;

    private void EnterCallback()
    {
        if (_callbackDepth.Value != 0)
            throw new GraphCallbackReentrancyException();
        _callbackDepth.Value = 1;
    }

    private void ExitCallback() => _callbackDepth.Value = 0;

    private GraphOperationException CreateFailure(string operation, string phase, Exception exception)
    {
        var diagnostic = new GraphDiagnostic(
            operation,
            phase,
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.ToString());
        _lastDiagnostic.Value = diagnostic;
        return new GraphOperationException(diagnostic, exception);
    }

    private static void RejectAsyncResult<TResult>(TResult result)
    {
        object? boxed = result;
        Type type = boxed?.GetType() ?? typeof(TResult);
        if (boxed is Task
            || type == typeof(ValueTask)
            || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            throw new InvalidOperationException(
                "GraphStore の callback は同期処理です。非同期処理には callback の外で入出力を完了させてください。");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
