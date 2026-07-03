using System.Collections.Concurrent;
using Quiver.Core;
using Quiver.Telemetry;
using Quiver.Index;
using Quiver.Storage.Records;
using Quiver.Storage.Wal;

namespace Quiver.Transactions;

internal sealed class TransactionManager : ITransactionManager
{
    private readonly IWriteAheadLog _wal;
    private readonly INodeStore _nodeStore;
    private readonly IRelationshipStore _relStore;
    private readonly IPropertyStore _propStore;
    private readonly IIndexManager _indexManager;
    private IAdjacencyBlockStore? _adjStore;
    private readonly IGraphAccessMethods _access;
    // abort / コミット失敗時のインプロセス undo を担う。null のときは undo 無し。
    private readonly AbortUndoHandler? _undoHandler;
    private readonly LockManager _nodeLocks = new();
    private readonly LockManager _relLocks = new();
    private readonly LockManager _indexLocks = new();
    private readonly ConcurrentDictionary<long, Transaction> _active = new();
    private long _nextTxId;

    // チェックポイント契機。EnableCheckpointing で配線される。
    private Checkpointer? _checkpointer;
    private long _checkpointThresholdBytes;
    private long _lastCheckpointBytes;
    private readonly object _checkpointGate = new();

    // Adaptive ポリシー時のみ非 null。OnCommit ごとに per-tx WAL byte delta を
    // サンプリングして threshold を更新する。
    private AdaptiveCheckpointController? _adaptiveController;
    // 直前 OnCommit 時の _wal.BytesWritten。次回 OnCommit でこれとの差分を tx サンプルとする。
    private long _lastSampledWalBytes;

    // ロック戦略 (ExclusiveOnly / ReaderWriter) と timeout を transaction へ流す。
    private readonly LockingMode _lockingMode;
    private readonly TimeSpan _lockTimeout;

    // デッドロック検出器 (null = 無効)。Dispose で停止。
    private DeadlockDetector? _deadlockDetector;

    // dotnet-counters の active-tx-count / current-checkpoint-threshold-bytes に値を
    // 流し込む provider 登録ハンドル。Dispose で解除して別インスタンスとの混線を防ぐ。
    private readonly IDisposable _activeTxCountRegistration;
    private readonly IDisposable _checkpointThresholdRegistration;

    // MVCC visibility 用。Begin / OnCommit の atomicity を保護するゲート。
    // Begin は (txId 採番 + activeAtBegin 集合のキャプチャ + _active への登録) を、
    // OnCommit は (registry.MarkCommitted + _active からの除去) を 1 ブロックで行う。
    // これにより新規 snapshot が「コミット済みかつ active には残っていない」状態を観測する。
    private readonly object _snapshotGate = new();
    private readonly CommittedTxRegistry _committed;

    // SSN 用の version sidecar (Serializable のときのみ Transaction に渡して使う)。
    private readonly IEntityVersionStore? _nodeVersions;
    private readonly IEntityVersionStore? _relVersions;
    // Serializable commit の pre-commit 検証 + post-commit スタンプ書き戻しを
    // 直列化するゲート。並行 Serializable commit 間で version スタンプの read-modify-write を保護する。
    private readonly object _ssnCommitGate = new();
    // 全 commit に単調な commit stamp c(T) を採番する大域クロック。SSN の π/η は
    // begin-order TxId ではなくこの commit-order stamp 空間で計算する。Serializable tx は
    // SSN 検証時 (SsnCommitGate 下)、SI/RC tx は OnCommit 時に採番する。
    private long _commitStamp;
    // creator TxId.Value → 採番済み commit stamp。Serializable tx の read が読んだバージョンの
    // creator cstamp を解決するのに使う。Bootstrap (= bulk load / 既定 xmin) は時刻 0 として seed。
    private readonly ConcurrentDictionary<long, long> _txCstamp = new()
    {
        [TransactionId.Bootstrap.Value] = 0,
    };

    public TransactionManager(
        IWriteAheadLog wal,
        INodeStore nodeStore,
        IRelationshipStore relStore,
        IPropertyStore propStore,
        IIndexManager indexManager,
        IAdjacencyBlockStore? adjStore = null,
        IGraphAccessMethods? access = null,
        AbortUndoHandler? undoHandler = null,
        LockingMode lockingMode = LockingMode.ExclusiveOnly,
        TimeSpan? lockTimeout = null,
        TimeSpan? deadlockDetectionInterval = null,
        CommittedTxRegistry? committedRegistry = null,
        IEntityVersionStore? nodeVersions = null,
        IEntityVersionStore? relVersions = null)
    {
        _wal = wal;
        _nodeStore = nodeStore;
        _relStore = relStore;
        _propStore = propStore;
        _indexManager = indexManager;
        _adjStore = adjStore;
        _access = access ?? InlineGraphAccessMethods.Instance;
        _undoHandler = undoHandler;
        _lockingMode = lockingMode;
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        _committed = committedRegistry ?? new CommittedTxRegistry();
        _nodeVersions = nodeVersions;
        _relVersions = relVersions;
        // _nextTxId は最初の Increment で 1 を返す (= Bootstrap.Value)。
        // Bootstrap は予約済みなので、最初の "ユーザ" tx が 2 から始まるよう offset しておく。
        _nextTxId = TransactionId.Bootstrap.Value + 1;
        if (deadlockDetectionInterval is { } interval && interval > TimeSpan.Zero)
        {
            _deadlockDetector = new DeadlockDetector(
                new[] { _nodeLocks, _relLocks, _indexLocks }, interval);
        }
        // gauge provider 登録 (PollingCounter から sum-of-providers として参照される)。
        _activeTxCountRegistration =
            QuiverEventSource.Log.RegisterActiveTxCountProvider(() => _active.Count);
        _checkpointThresholdRegistration =
            QuiverEventSource.Log.RegisterCheckpointThresholdProvider(() => CurrentCheckpointThresholdBytes);
    }

    /// <summary>
    /// backend factory から recovery 経路で WAL を走査して構築済みの registry を注入する経路。
    /// </summary>
    internal CommittedTxRegistry CommittedRegistry => _committed;

    /// <summary>Serializable commit を直列化するゲート (Transaction から参照)。</summary>
    internal object SsnCommitGate => _ssnCommitGate;

    /// <summary>
    /// 指定 tx の commit stamp c(T) を返す。未採番なら大域クロックから 1 つ採番する
    /// (冪等)。Serializable tx は SSN 検証時にこれを呼んで c(T) を確定させ、その後の OnCommit
    /// 経由の再呼び出しでは同じ値を返す。
    /// </summary>
    internal long GetOrAssignCommitStamp(long txIdValue)
    {
        if (_txCstamp.TryGetValue(txIdValue, out var existing)) return existing;
        long assigned = Interlocked.Increment(ref _commitStamp);
        return _txCstamp.GetOrAdd(txIdValue, assigned);
    }

    /// <summary>
    /// 大域クロックから新しい候補 commit stamp を 1 つ採番する (単調)。SSN の commit stamp
    /// は最終的に <c>cstamp(T) = π(T)</c> (候補で上限を取った値) になるため、候補採番と確定 (
    /// <see cref="SetCommitStamp"/>) を分離する。候補値は restart 連続性用の高水位としても使う。
    /// </summary>
    internal long NextCommitStamp() => Interlocked.Increment(ref _commitStamp);

    /// <summary>
    /// Serializable tx の最終 commit stamp (= π(T)) を確定して登録する。後続 tx の
    /// <see cref="CommitStampOf"/> はこの値を返し、SSN の η/π 伝播が推移的に効く。
    /// </summary>
    internal void SetCommitStamp(long txIdValue, long cstamp) => _txCstamp[txIdValue] = cstamp;

    /// <summary>
    /// 現在の commit-stamp クロック値 (スナップショット下限)。Serializable tx が Begin 時に
    /// 捕捉し、読んだバージョンの overwriter cstamp (v.sstamp) を π に反映するかの判定に使う:
    /// <c>v.sstamp &gt; snapshotClock</c> のときだけ「自分が読んだのは上書き前の版」= rw-antidependency
    /// として π を下げる。これにより既に commit 済みの上書き後の版を読むだけの retry が
    /// stale な sstamp で false-abort するのを防ぐ (safe-retry, Theorem 7)。
    /// </summary>
    internal long CurrentCommitStampClock => Volatile.Read(ref _commitStamp);

    /// <summary>
    /// 再起動時に永続化済みの commit-stamp 高水位までクロックを巻き上げる。
    /// これにより新規 commit stamp は過去に永続化されたどの version stamp よりも大きくなり、
    /// 旧/新 stamp 空間の混在 (= 再起動後の false-abort ストーム) を防ぐ。既に進んでいれば no-op。
    /// </summary>
    internal void SeedCommitStamp(long highWater)
    {
        long current = Volatile.Read(ref _commitStamp);
        while (highWater > current)
        {
            long prev = Interlocked.CompareExchange(ref _commitStamp, highWater, current);
            if (prev == current) return;
            current = prev;
        }
    }

    /// <summary>
    /// creator TxId の commit stamp を返す。未知 (= recovery 前のプロセスで commit された
    /// バージョン等) は 0 (= 太古の committed) として扱う。SSN の read-side η 下限として安全側。
    /// </summary>
    internal long CommitStampOf(long creatorTxIdValue)
    {
        if (creatorTxIdValue == 0) return 0;
        return _txCstamp.TryGetValue(creatorTxIdValue, out var c) ? c : 0;
    }

    /// <summary>
    /// recovery で観測した最大 TxId + 1 まで _nextTxId を巻き上げる。
    /// 既に進んでいる場合は no-op。
    /// </summary>
    internal void AdvanceNextTxIdAtLeast(long minimum)
    {
        long current = Volatile.Read(ref _nextTxId);
        while (minimum > current)
        {
            long prev = Interlocked.CompareExchange(ref _nextTxId, minimum, current);
            if (prev == current) return;
            current = prev;
        }
    }

    /// <summary>
    /// 次に採番される TxId。クリーン終了時に container へ committed TxId 高水位
    /// として永続化し、WAL 削除後の reopen で visibility horizon / 採番起点を復元する。
    /// </summary>
    internal long PeekNextTxId() => Volatile.Read(ref _nextTxId);

    /// <summary>テスト / 診断用。null のときは検出器無効。</summary>
    internal DeadlockDetector? DeadlockDetector => _deadlockDetector;

    public int ActiveCount => _active.Count;

    /// <summary>
    /// vacuum 用 visibility horizon。「これ未満の TxId が刻まれた dead version は
    /// 物理回収しても誰のスナップショットも壊さない」境界を返す。
    /// 計算: 現在 active な tx の <see cref="Transaction.Id"/> 最小値。active が 0 件なら
    /// 次に採番される TxId (= _nextTxId)。戻り値以上の TxId を xmax に持つ dead version は
    /// まだ古い snapshot から参照され得る可能性があるので vacuum は触れてはならない。
    /// </summary>
    internal long GetVisibilityHorizon()
    {
        long oldest = long.MaxValue;
        foreach (var tx in _active.Values)
            if (tx.Id.Value < oldest) oldest = tx.Id.Value;
        if (oldest == long.MaxValue)
            return Volatile.Read(ref _nextTxId);
        return oldest;
    }

    public long OldestActiveLsn
    {
        get
        {
            long oldest = long.MaxValue;
            foreach (var tx in _active.Values)
                if (tx.SnapshotLsn < oldest) oldest = tx.SnapshotLsn;
            return oldest == long.MaxValue ? _wal.FlushedLsn : oldest;
        }
    }

    public ITransaction Begin(IsolationLevel level = IsolationLevel.SnapshotIsolation)
    {
        // snapshot (txId 採番 + activeAtBegin 集合キャプチャ + _active 登録) を 1 ブロックで。
        // _active 登録まで含めないと、Begin 中の自身を他の Begin の activeAtBegin に含めるかどうかが
        // 競合する。MarkCommitted も同じゲートを取るので「コミット直後の tx が見えるかどうか」は
        // ゲート取得順で決まり、Postgres SI 風になる。
        TransactionId txId;
        SnapshotState snapshot;
        long snapshotLsn = _wal.FlushedLsn;
        Transaction tx;
        lock (_snapshotGate)
        {
            txId = new TransactionId(Interlocked.Increment(ref _nextTxId) - 1);
            var activeAtBegin = new HashSet<long>(_active.Keys);
            snapshot = new SnapshotState(txId, activeAtBegin);
            _wal.Append(WalRecordType.Begin, txId, ReadOnlySpan<byte>.Empty);
            tx = new Transaction(txId, level, snapshotLsn,
                _wal, _nodeLocks, _relLocks, _indexLocks, this,
                _nodeStore, _relStore, _propStore, _indexManager, _adjStore, _access,
                _undoHandler, _lockingMode, _lockTimeout,
                snapshot, _committed, _nodeVersions, _relVersions);
            _active[txId.Value] = tx;
        }
        return tx;
    }

    /// <summary>
    /// チェックポイント契機を有効化する。<paramref name="thresholdBytes"/> 以上
    /// WAL が成長し、かつアクティブトランザクションが 0 になった時点でチェックポイントを打つ。
    /// <paramref name="thresholdBytes"/> が 0 以下のときはチェックポイントを行わない。
    /// </summary>
    internal void EnableCheckpointing(Checkpointer checkpointer, long thresholdBytes)
    {
        _checkpointer = checkpointer;
        _checkpointThresholdBytes = thresholdBytes;
        _lastCheckpointBytes = _wal.BytesWritten;
        _lastSampledWalBytes = _wal.BytesWritten;
    }

    /// <summary>
    /// Adaptive ポリシーを有効化する。<paramref name="controller"/> は per-tx の
    /// WAL byte delta を観測して内部 threshold を更新し、<see cref="MaybeCheckpoint"/> は
    /// 固定値ではなく controller の現在値を使う。<c>null</c> を渡すと Fixed 挙動に戻す
    /// (ホットスワップ可)。
    /// </summary>
    internal void SetAdaptiveController(AdaptiveCheckpointController? controller)
    {
        _adaptiveController = controller;
        _lastSampledWalBytes = _wal.BytesWritten;
    }

    /// <summary>
    /// ホットスワップ用。既存の checkpointer 配線は維持したまま threshold (Fixed) を
    /// 差し替える。Adaptive と Fixed の切り替えは <see cref="SetAdaptiveController"/> と
    /// 併用する (Fixed に戻す場合は <c>SetAdaptiveController(null)</c> + 本メソッドで新しい
    /// 固定値を渡す)。
    /// </summary>
    internal void SetFixedThreshold(long thresholdBytes)
    {
        _checkpointThresholdBytes = thresholdBytes;
    }

    /// <summary>
    /// 現在採用中のチェックポイント threshold (バイト単位)。Adaptive のときは
    /// controller の最新値を返す。Adaptive controller が warmup 中 (サンプル不足) のときは
    /// initial threshold をそのまま返す。
    /// </summary>
    internal long CurrentCheckpointThresholdBytes
    {
        get
        {
            var controller = _adaptiveController;
            if (controller != null && controller.HasEnoughSamples)
                return controller.CurrentThresholdBytes;
            return _checkpointThresholdBytes;
        }
    }

    internal void OnCommit(TransactionId txId)
    {
        // MarkCommitted と _active 除去を 1 ブロックで。新規 Begin が
        // 「コミット済みかつ active 集合に居ない」状態を観測するための atomicity。
        lock (_snapshotGate)
        {
            _committed.MarkCommitted(txId);
            _active.TryRemove(txId.Value, out _);
        }

        // commit 済み tx に commit stamp を確定させる (SI/RC はここで初採番、
        // Serializable は SSN 検証時に採番済みなので冪等 no-op)。Serializable tx が
        // 後で読んだバージョンの creator cstamp を解決できるよう、全 commit を登録する。
        GetOrAssignCommitStamp(txId.Value);

        // Adaptive ポリシーが有効なら per-tx WAL delta をサンプルとして controller へ。
        // _wal.BytesWritten は単調増加。直前 OnCommit との差分が、本 tx が WAL に追記した量
        // (Begin / PageImage / Commit) の合計。並列 commit 経路では別 tx の延べバイト数が
        // 混在しうるが、移動平均で平準化されるため統計的に問題ない。
        var controller = _adaptiveController;
        if (controller != null)
        {
            long current = _wal.BytesWritten;
            long prev = Interlocked.Exchange(ref _lastSampledWalBytes, current);
            long delta = current - prev;
            if (delta > 0) controller.RecordTxBytes(delta);
        }

        MaybeCheckpoint();
    }

    internal void OnAbort(TransactionId txId)
    {
        // registry には登録しない (= visibility 判定で aborted = invisible)。
        lock (_snapshotGate)
        {
            _active.TryRemove(txId.Value, out _);
        }
    }

    /// <summary>
    /// コミット直後に呼ばれ、チェックポイント契機を満たしていれば同期的に実行する。
    /// チェックポイントはアクティブトランザクションが 0 のときにのみ打つ。これにより
    /// 全ダーティページがコミット済み (またはアボード済み — アボートはページを巻き戻さない
    /// 既存仕様) であることが保証され、シャープチェックポイントとして安全に WAL を truncate
    /// できる。未コミットトランザクションのダーティページをデータファイルへ流して
    /// しまうこともない。
    /// </summary>
    private void MaybeCheckpoint()
    {
        var checkpointer = _checkpointer;
        if (checkpointer == null) return;
        // 実効 threshold は Fixed 値か、Adaptive controller が warmup 完了後に返す値。
        long threshold = CurrentCheckpointThresholdBytes;
        if (threshold <= 0) return;

        // ロック外の安価な事前判定。
        if (!_active.IsEmpty) return;
        if (_wal.BytesWritten - Volatile.Read(ref _lastCheckpointBytes) < threshold)
            return;

        // 同時コミットによる二重実行を防ぐ。取得できなければ他スレッドが処理中なのでスキップ。
        if (!Monitor.TryEnter(_checkpointGate)) return;
        try
        {
            // ゲート内で再判定 (TOCTOU 回避)。
            if (!_active.IsEmpty) return;
            threshold = CurrentCheckpointThresholdBytes;
            if (threshold <= 0) return;
            if (_wal.BytesWritten - _lastCheckpointBytes < threshold) return;

            checkpointer.Checkpoint();
            Volatile.Write(ref _lastCheckpointBytes, _wal.BytesWritten);
        }
        finally
        {
            Monitor.Exit(_checkpointGate);
        }
    }

    /// <summary>
    /// 使用中の隣接ストア参照を差し替える。バックエンドが compact rebuild 後に呼ぶ。
    /// トランザクションは <see cref="Begin"/> 時に参照を固定するため、
    /// <see cref="ActiveCount"/> が 0 の間だけ安全に実行できる。
    /// </summary>
    internal void SwapAdjacencyStore(IAdjacencyBlockStore? next) => _adjStore = next;

    /// <summary>
    /// <see cref="GraphDatabase.CreateSnapshot"/> の前段で呼ばれ、ベストエフォートで
    /// シャープチェックポイントを 1 回起動する。アクティブトランザクションが居る場合は
    /// (シャープチェックポイントの不変条件を破らないよう) スキップする。スキップしても
    /// snapshot 自体は WAL から redo / undo して target を整合させるため correctness には
    /// 影響しないが、checkpoint 直後だと target 側 recovery の WAL 走査範囲が短くて済む。
    /// </summary>
    internal void RequestCheckpoint()
    {
        var checkpointer = _checkpointer;
        if (checkpointer == null) return;
        if (!_active.IsEmpty) return;

        if (!Monitor.TryEnter(_checkpointGate)) return;
        try
        {
            if (!_active.IsEmpty) return;
            checkpointer.Checkpoint();
            Volatile.Write(ref _lastCheckpointBytes, _wal.BytesWritten);
        }
        finally
        {
            Monitor.Exit(_checkpointGate);
        }
    }

    public void Dispose()
    {
        _deadlockDetector?.Dispose();
        _deadlockDetector = null;
        // gauge provider を解除して別インスタンス / 二重登録による加算ズレを防ぐ。
        _activeTxCountRegistration.Dispose();
        _checkpointThresholdRegistration.Dispose();
    }
}
