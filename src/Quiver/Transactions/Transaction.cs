using System.Diagnostics;
using Quiver.Core;
using Quiver.Telemetry;
using Quiver.Index;
using Quiver.Storage.Records;
using Quiver.Storage.Wal;

namespace Quiver.Transactions;

internal sealed class Transaction : ITransaction
{
    private readonly IWriteAheadLog _wal;
    private readonly WalWriteSet _walWriteSet;
    private readonly LockManager _vertexLocks;
    private readonly LockManager _edgeLocks;
    private readonly LockManager _nexusLocks;
    private readonly LockManager _indexLocks;
    private readonly TransactionManager _manager;
    private readonly TxVertexStore _vertices;
    private readonly TxEdgeStore _edges;
    private readonly TxNexusStore _nexuses;
    private readonly IIncidenceStore _incidences;
    private readonly IVertexIncidenceHeadStore _vertexIncidenceHeads;
    private readonly TxPropertyStore _properties;
    private readonly TxIndexManager _indexes;
    private readonly IAdjacencySegmentStore? _adjStore;
    private readonly ICoMembershipBlockStore? _coMembershipStore;
    private readonly IGraphAccessMethods _access;
    // null でない場合、abort / コミット失敗時にキャプチャ済み before-image を
    // データファイルへ書き戻し、ストアメタを再ロードしてインプロセス undo を行う。
    // 索引 PagedFile も EnableWalLogging により before-image capture 対象なので、
    // 本ハンドラだけで data + index 両方の in-process abort が完結する ( で IndexUndoLog 撤去)。
    private readonly AbortUndoHandler? _undoHandler;
    private List<Action>? _onCommitted;
    private List<Action>? _onRolledBack;
    private TransactionState _state;
    private int _usageOwnerThreadId;
    private int _usageDepth;

    // savepoint 管理。SavepointId.Value (連番) → transaction-owned write set のバケット index。
    // RollbackTo で巻き戻しても savepoint 自体は消費しないので、Value は同じレベルで再利用される。
    // ReleaseSavepoint または親 savepoint の Rollback/Release で初めて無効化される。
    private long _nextSavepointId;
    private List<(long Id, int Level)>? _savepoints;

    // SSN (Serializable) のときのみ非 null。read/write hook が η/π を更新し、
    // Commit の pre-commit 検証 + post-commit スタンプ書き戻しで使う。
    private readonly SsnContext? _ssn;
    private readonly IEntityVersionStore? _vertexVersions;
    private readonly IEntityVersionStore? _edgeVersions;
    private readonly IEntityVersionStore? _nexusVersions;
    // Begin 時の commit-stamp クロック (snapshot 下限)。読んだ版の v.sstamp を π に
    // 反映するかの判定に使う (詳細は TransactionManager.CurrentCommitStampClock)。
    private readonly long _ssnSnapshotCstamp;

    public TransactionId Id { get; }
    public IsolationLevel Level { get; }
    public long SnapshotLsn { get; }
    public TransactionState State => _state;

    // 列スキャン集約が直接可視性を判定できるよう snapshot / committed を公開する。
    private readonly SnapshotState _snapshot;
    private readonly CommittedTxRegistry? _committed;
    public SnapshotState Snapshot => _snapshot;
    public CommittedTxRegistry? Committed => _committed;

    public IVertexStore Vertices => _vertices;
    public IEdgeStore Edges => _edges;
    public INexusStore Nexuses => _nexuses;
    public IIncidenceStore Incidences => _incidences;
    public IVertexIncidenceHeadStore VertexIncidenceHeads => _vertexIncidenceHeads;
    public IPropertyStore Properties => _properties;
    public IIndexManager Indexes => _indexes;
    public IAdjacencySegmentStore? AdjacencySegments => _adjStore;
    public ICoMembershipBlockStore? CoMembershipBlocks
        => _nexuses.HasPendingViewAdds ? null : _coMembershipStore;
    public IGraphAccessMethods Access => _access;

    internal Transaction(
        TransactionId id, IsolationLevel level, long snapshotLsn,
        IWriteAheadLog wal,
        LockManager vertexLocks, LockManager edgeLocks, LockManager nexusLocks,
        LockManager indexLocks,
        TransactionManager manager,
        IVertexStore vertexStore, IEdgeStore edgeStore,
        INexusStore nexusStore, IIncidenceStore incidenceStore,
        IVertexIncidenceHeadStore vertexIncidenceHeadStore,
        IPropertyStore propStore, IIndexManager indexManager,
        IAdjacencySegmentStore? adjStore = null,
        IGraphAccessMethods? access = null,
        AbortUndoHandler? undoHandler = null,
        LockingMode lockingMode = LockingMode.ExclusiveOnly,
        TimeSpan? lockTimeout = null,
        SnapshotState snapshot = default,
        CommittedTxRegistry? committed = null,
        IEntityVersionStore? vertexVersions = null,
        IEntityVersionStore? edgeVersions = null,
        IEntityVersionStore? nexusVersions = null,
        ICoMembershipBlockStore? coMembershipStore = null,
        PersistentEdgeDeltaStore? edgeDeltas = null)
    {
        Id = id; Level = level; SnapshotLsn = snapshotLsn;
        _wal = wal;
        _vertexLocks = vertexLocks; _edgeLocks = edgeLocks; _nexusLocks = nexusLocks;
        _indexLocks = indexLocks;
        _manager = manager;
        _adjStore = adjStore;
        _coMembershipStore = coMembershipStore;
        _access = access ?? InlineGraphAccessMethods.Instance;
        _undoHandler = undoHandler;
        _state = TransactionState.Active;
        var timeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        // Serializable かつ MVCC コンテキストがあるときのみ SSN を起動する。
        // sidecar が無い (旧テスト経路など) 場合は SI と同じ挙動に縮退する。
        _vertexVersions = vertexVersions;
        _edgeVersions = edgeVersions;
        _nexusVersions = nexusVersions;
        _ssn = (level == IsolationLevel.Serializable && committed != null
            && vertexVersions != null && edgeVersions != null)
            ? new SsnContext() : null;
        // Serializable のときだけ Begin 時点の commit-stamp クロックを捕捉する
        // (ctor は TransactionManager.Begin の _snapshotGate 下で走るため一貫した下限)。
        _ssnSnapshotCstamp = _ssn != null ? manager.CurrentCommitStampClock : 0;
        // per-tx ambient コンテキストを Tx wrapper にも持たせ、各操作直前に
        // MvccContext を再アクティベートする (複数 tx 操作を交互に
        // 行う場合の ambient context の取り違えを防ぐ)。
        var snap = snapshot.ActiveAtBegin == null ? SnapshotState.Empty : snapshot;
        _snapshot = snap;
        _committed = committed;
        _vertices = new TxVertexStore(vertexStore, vertexLocks, id, lockingMode, timeout, snap, committed, _ssn);
        _edges = new TxEdgeStore(
            edgeStore, edgeLocks, id, _vertices, lockingMode, timeout, snap, committed, _ssn,
            edgeDeltas);
        _nexuses = new TxNexusStore(nexusStore, incidenceStore, vertexIncidenceHeadStore,
            nexusLocks, vertexLocks, id, lockingMode, timeout, snap, committed, _ssn,
            coMembershipStore);
        _incidences = incidenceStore;
        _vertexIncidenceHeads = vertexIncidenceHeadStore;
        _properties = new TxPropertyStore(propStore, id, snap, committed, _ssn);
        _indexes = new TxIndexManager(indexManager, indexLocks, id, timeout);
        _walWriteSet = WalWriteSetContext.Begin(wal, id);
        // MVCC ambient コンテキスト開始 (Tx wrapper を介さない経路のため)。
        // null なら旧テスト等の互換経路として MvccContext を起動しない (= Bootstrap fallback)。
        if (committed != null)
        {
            MvccContext.Begin(id, snap, committed, _ssn);
        }
    }

    public TransactionUsageLease EnterUsage()
    {
        WalWriteSetContext.Activate(_walWriteSet);
        int threadId = Environment.CurrentManagedThreadId;
        int owner = Volatile.Read(ref _usageOwnerThreadId);
        if (owner == threadId)
        {
            _usageDepth++;
            return new TransactionUsageLease(this);
        }

        if (Interlocked.CompareExchange(ref _usageOwnerThreadId, threadId, 0) != 0)
            throw new TransactionException(
                $"Transaction {Id.Value} is already being used by another thread.");

        _usageDepth = 1;
        return new TransactionUsageLease(this);
    }

    internal void ExitUsage()
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _usageOwnerThreadId) != threadId)
            throw new TransactionException(
                $"Transaction {Id.Value} usage scope ended on a different thread.");

        int depth = _usageDepth - 1;
        if (depth <= 0)
        {
            _usageDepth = 0;
            Volatile.Write(ref _usageOwnerThreadId, 0);
            return;
        }

        _usageDepth = depth;
    }

    public void Commit()
    {
        using var usage = EnterUsage();
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot commit: transaction is not Active.");
        _state = TransactionState.Preparing;
        // span + duration histogram。AlwaysOnSampler が無い環境 (StartActivity が null) では
        // ActivitySource はコストゼロで Stopwatch のみ走る。
        using var activity = QuiverTelemetry.TransactionActivitySource.StartActivity(
            "tx.commit", ActivityKind.Internal);
        activity?.SetTag("quiver.tx.id", Id.Value);
        var sw = Stopwatch.StartNew();
        bool durableCommitted = false;
        bool managerEntered = false;
        try
        {
            // Serializable のときは PageImage を WAL へ流す前に SSN の
            // exclusion-window 検証を行う。違反なら SerializabilityException を投げ、
            // 下の catch が in-place rollback + Abort を行う。検証を通ったら同じ
            // critical section で post-commit スタンプを sidecar に書き戻し、それも
            // 本 tx の PageImage として WAL に乗せて durable にする。
            if (_ssn != null) SsnValidateAndStamp();
            // UnpinDirty はページイメージをトランザクションバッファにコアレスするだけ。
            // ここで全 PageImage を WAL へ追記し、その後に Commit レコードを書く。
            // Commit を最後に書くことで、recovery はコミット済みトランザクションの
            // ページイメージのみを replay する。
            // property HWM/free-head は transaction 内でまとめ、commit の page image 確定前に 1 回だけ書く。
            _properties.FlushMeta();
            _walWriteSet.FlushPending();
            long lsn = _wal.Append(WalRecordType.Commit, Id, ReadOnlySpan<byte>.Empty);
            _wal.FlushTo(lsn);
            durableCommitted = true;
            WalWriteSetContext.End(_walWriteSet);
            // MVCC ambient コンテキスト終了 (これ以降このスレッドは
            // ベンチ / bulk loader 等の Bootstrap fallback 経路に戻る)。
            MvccContext.End();
            // durable commit を観測できる境界より前に導出ビュー差分を公開する。
            // これ以降に開始する reader は正本とビューを同じ状態で参照できる。
            _nexuses.PublishPendingViewAdds();
            ReleaseAllLocks();
            _state = TransactionState.Committed;
            managerEntered = true;
            _manager.OnCommit(Id);
            QuiverTelemetry.TxCommitCount.Add(1);
            QuiverTelemetry.TxCommitDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            QuiverEventSource.Log.TxCommit();
            QuiverEventSource.Log.TxCommitted(Id.Value, sw.Elapsed.TotalMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            // Commit の fsync 後は結果を abort へ戻せない。checkpoint や通知処理の失敗が
            // 呼び出し元へ伝播しても、明示 Commit を winner とする状態を維持する。
            if (durableCommitted)
            {
                try { WalWriteSetContext.End(_walWriteSet); } catch { }
                try { MvccContext.End(); } catch { }
                try { _nexuses.PublishPendingViewAdds(); } catch { }
                try { ReleaseAllLocks(); } catch { }
                _state = TransactionState.Committed;
                if (!managerEntered)
                {
                    try { _manager.OnCommit(Id); } catch { }
                }
                QuiverTelemetry.TxCommitCount.Add(1);
                QuiverTelemetry.TxCommitDurationMs.Record(sw.Elapsed.TotalMilliseconds);
                QuiverEventSource.Log.TxCommit();
                QuiverEventSource.Log.TxCommitted(Id.Value, sw.Elapsed.TotalMilliseconds);
                activity?.SetStatus(ActivityStatusCode.Error, "post-commit processing failed");
                FireHooks(_onCommitted);
                throw;
            }

            // WAL flush 失敗などで Commit が途中失敗した場合は rollback として公開し、
            // 登録済み OnRolledBack フックから一貫した結果が見えるようにする。
            // コンテキスト破棄前にページ変更をその場で戻し、WAL 上でも abort を記録する。
            try { RollBackInPlace(); } catch { }
            // drain 前に自分の PageImage を coalesce バッファから除去 (最適化)。
            try { _wal.EvictCoalescedPageImagesFor(Id); } catch { }
            try { _wal.Append(WalRecordType.Abort, Id, ReadOnlySpan<byte>.Empty); } catch { }
            try { WalWriteSetContext.End(_walWriteSet); } catch { }
            try { MvccContext.End(); } catch { }
            try { ReleaseAllLocks(); } catch { }
            _state = TransactionState.Aborted;
            _manager.OnAbort(Id);
            QuiverTelemetry.TxAbortCount.Add(1);
            QuiverTelemetry.TxAbortDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            QuiverEventSource.Log.TxAbort();
            QuiverEventSource.Log.TxCommitFailed(
                Id.Value,
                ex.Message,
                ex.GetType().FullName ?? ex.GetType().Name);
            activity?.SetStatus(ActivityStatusCode.Error, "commit failed → rolled back");
            FireHooks(_onRolledBack);
            throw;
        }
        FireHooks(_onCommitted);
    }

    public void Abort()
    {
        using var usage = EnterUsage();
        if (_state is TransactionState.Committed or TransactionState.Aborted) return;
        // abort span + duration。Commit と同じ ActivitySource を共有。
        using var activity = QuiverTelemetry.TransactionActivitySource.StartActivity(
            "tx.abort", ActivityKind.Internal);
        activity?.SetTag("quiver.tx.id", Id.Value);
        var sw = Stopwatch.StartNew();
        // in-process undo では取得済み before-image をデータファイルへ戻し、
        // ページベースストアのメタデータを再読込する。破棄したVertex、エッジ、
        // プロパティが後続トランザクションから見えないようにするため、
        // write set の before-image を破棄する前に実行する。
        RollBackInPlace();
        // 共有 coalesce バッファに残った自分の PageImage を破棄してから Abort を書く。
        // (Append(Abort) の drain で aborted tx の after-image が WAL に漏れるのを抑制する最適化。
        // 漏れても recovery で abortedTxs により skip されるため correctness には影響しない。)
        _wal.EvictCoalescedPageImagesFor(Id);
        _wal.Append(WalRecordType.Abort, Id, ReadOnlySpan<byte>.Empty);
        WalWriteSetContext.End(_walWriteSet);
        MvccContext.End();
        ReleaseAllLocks();
        _state = TransactionState.Aborted;
        _manager.OnAbort(Id);
        QuiverTelemetry.TxAbortCount.Add(1);
        QuiverTelemetry.TxAbortDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        QuiverEventSource.Log.TxAborted(Id.Value, sw.Elapsed.TotalMilliseconds);
        FireHooks(_onRolledBack);
    }

    // このトランザクションで取得した before-image をその場で適用する。
    // 読み取り専用トランザクションと undo handler のないバックエンドでは何もしない。
    private void RollBackInPlace()
    {
        if (_undoHandler == null) return;
        var beforeImages = _walWriteSet.GetAllBeforeImagesOldestWins();
        if (beforeImages.Count > 0)
            _undoHandler.Undo(beforeImages);
    }

    // ==================== SSN コミットプロトコル ====================

    /// <summary>
    /// SSN (Wang et al. DaMoN'15) Algorithm 1 の commit 時検証 + post-commit スタンプ書き戻し。
    /// π/η は <see cref="TransactionManager"/> の大域 commit-stamp クロックで計算する
    /// (begin-order の TxId ではなく commit-order)。read/write hook が収集した read/write set を
    /// もとに、creator cstamp / reader pstamp / overwriter sstamp を畳み込んで exclusion window
    /// (π(T) &gt; η(T)) を判定する。検証 + 書き戻しは <see cref="TransactionManager.SsnCommitGate"/>
    /// 下で直列化し、並行 Serializable commit 間の version スタンプ read-modify-write を保護する。
    /// <para>read 捕捉は <see cref="ISsnReadSink"/> をストアの物理読み取り点 (VertexStore /
    /// EdgeStore の Read・Scan) に挿しているため、直接 Read だけでなく traversal の隣接走査・
    /// scan・index seek 後のレコード読みも一律 read-set に入る (= rw-antidependency の取りこぼしなし)。</para>
    /// <para>仕様上の限界 (設計でスコープ外、index versioning / 別タスク前提): phantom protection は
    /// 対象外 — 述語に新規一致する行や隣接の増加 (= 既存バージョンの読みではない) は検出しない。
    /// lock は SSN と併存し撤去しない (将来別タスク)。</para>
    /// <para>実装上の割り切り (いずれも安全側 = false-abort 方向で、missed-anomaly は起こさない):
    /// (1) 競合粒度は Vertex / Edge 単位で per-property ではない (同一Vertexの別プロパティ同士も
    /// 衝突扱い = over-abort)。(2) early-abort は入れず commit 時に一括判定 (perf 最適化の見送りで
    /// correctness 不変)。(3) commit-stamp クロックはプロセスローカルで再起動時リセット
    /// (永続化/復元せず)。再起動を跨ぐと旧/新 stamp 空間が混在し得るが η は下限・π は上限なので
    /// false-abort のみ (safe-retry で回復可能)。</para>
    /// </summary>
    private void SsnValidateAndStamp()
    {
        var ssn = _ssn!;
        lock (_manager.SsnCommitGate)
        {
            // 候補 commit stamp (単調)。最終 cstamp(T) は下で π(T) に確定する。
            long candidate = _manager.NextCommitStamp();
            // commit-stamp 高水位を vertex sidecar ヘッダへ耐久化する。本 tx の
            // transaction-owned write set がまだ生きているので commit と同じ page-WAL 単位で永続化され、
            // 再起動後の Open でこの値からクロックを再開できる (旧/新 stamp 空間の混在を防ぐ)。
            // 候補は gate 下で単調増加するため最新書き込みが最高値。
            _vertexVersions!.WriteCommitStampHighWater(candidate);
            long eta = 0;                 // η(T)
            long pi = long.MaxValue;      // π(T)

            // 読んだバージョン: creator cstamp を η に、上書き済みなら overwriter sstamp を π に。
            foreach (var r in ssn.Reads)
            {
                var meta = ReadMeta(r);
                if (meta.Xmin != 0 && meta.Xmin != Id.Value)
                {
                    long c = _manager.CommitStampOf(meta.Xmin);
                    if (c > eta) eta = c;
                }
                // overwriter cstamp を π に反映するのは「上書きが自分の snapshot より後」=
                // 自分が読んだのが上書き前の版のときだけ (rw-antidependency)。既に commit 済みの
                // 上書き後の版を読んだだけなら rw 依存は無いので π を下げない (safe-retry)。
                if (meta.Sstamp < pi && meta.Sstamp > _ssnSnapshotCstamp) pi = meta.Sstamp;
            }
            // 上書きしたバージョン: その reader (v.pstamp) は self への r:w in-edge → η に。
            foreach (var w in ssn.Writes)
            {
                long p = ReadMeta(w).Pstamp;
                if (p > eta) eta = p;
            }
            // π = min(π, candidate)。
            if (candidate < pi) pi = candidate;

            // exclusion window 違反なら abort。
            if (eta >= pi)
                throw new SerializabilityException(Id,
                    $"Transaction {Id.Value} would violate serializability (η={eta} ≥ π={pi}).");

            // 最終 commit stamp = π(T)。これを後続 tx が CommitStampOf / version stamp
            // 経由で観測することで η/π 伝播が推移的になり、3-cycle 以上の dangerous structure も
            // 検出できる (fresh counter のままだと推移性が壊れる)。
            long cstamp = pi;
            _manager.SetCommitStamp(Id.Value, cstamp);

            // post-commit: 読んだバージョンに reader cstamp (π(T)) を、上書きしたバージョンに
            // overwriter cstamp (π(T)) を記録する。これらの書き込みは transaction-owned write set がまだ
            // 生きているため本 tx の PageImage として WAL に乗り、commit と一体で durable になる。
            foreach (var r in ssn.Reads)
            {
                var store = StoreFor(r.Kind);
                if (store == null) continue;
                if (cstamp > store.Read(r.LocalId).Pstamp)
                    store.UpdatePstamp(r.LocalId, cstamp);
            }
            foreach (var w in ssn.Writes)
            {
                var store = StoreFor(w.Kind);
                if (store == null) continue;
                if (cstamp < store.Read(w.LocalId).Sstamp)
                    store.UpdateSstamp(w.LocalId, cstamp);
            }
        }
    }

    private EntityVersionMeta ReadMeta(EntityId id)
    {
        var store = StoreFor(id.Kind);
        return store == null ? EntityVersionMeta.Unset : store.Read(id.LocalId);
    }

    private IEntityVersionStore? StoreFor(EntityKind kind) => kind switch
    {
        EntityKind.Vertex => _vertexVersions,
        EntityKind.Edge => _edgeVersions,
        EntityKind.Nexus => _nexusVersions,
        _ => null,
    };

    // ==================== セーブポイント ====================

    public SavepointId Savepoint(string? name = null)
    {
        using var usage = EnterUsage();
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot create savepoint: transaction is not Active.");
        // partial undo が property HWM/free-head をこの境界へ戻せるよう、親 level の page image として確定する。
        _properties.FlushMeta();
        int level = _walWriteSet.PushSavepoint();
        long id = ++_nextSavepointId;
        (_savepoints ??= new List<(long, int)>()).Add((id, level));
        return new SavepointId(id, name);
    }

    public void RollbackTo(SavepointId savepoint)
    {
        using var usage = EnterUsage();
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot rollback to savepoint: transaction is not Active.");
        int index = FindSavepointIndex(savepoint.Value);
        if (index < 0)
            throw new TransactionException($"Savepoint {savepoint} is not valid in this transaction.");

        int level = _savepoints![index].Level;
        // 上位 savepoint も同時に無効化する (PostgreSQL / SQL 標準: ROLLBACK TO Sn は
        // Sn より新しい全ての savepoint も解放する)。Sn 自身は消費しない。
        _savepoints.RemoveRange(index + 1, _savepoints.Count - index - 1);

        var beforeImages = _walWriteSet.RollbackToSavepoint(level);
        if (_undoHandler != null)
        {
            if (beforeImages.Count > 0) _undoHandler.UndoPartial(beforeImages);
        }
        // savepoint undo 後の正本から、この transaction がまだ保持する create 差分だけを
        // 再収集する。ID slot が同じ transaction 内で再利用されても古い member を公開しない。
        _nexuses.RefreshPendingViewAdds();
    }

    public void ReleaseSavepoint(SavepointId savepoint)
    {
        using var usage = EnterUsage();
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot release savepoint: transaction is not Active.");
        int index = FindSavepointIndex(savepoint.Value);
        if (index < 0)
            throw new TransactionException($"Savepoint {savepoint} is not valid in this transaction.");

        int level = _savepoints![index].Level;
        // Release した savepoint より新しい savepoint も同時に無効化する (SQL 標準準拠)。
        _savepoints.RemoveRange(index, _savepoints.Count - index);
        _walWriteSet.ReleaseSavepoint(level);
    }

    private int FindSavepointIndex(long id)
    {
        if (_savepoints == null) return -1;
        for (int i = 0; i < _savepoints.Count; i++)
        {
            if (_savepoints[i].Id == id) return i;
        }
        return -1;
    }

    public void Dispose()
    {
        using var usage = EnterUsage();
        if (_state == TransactionState.Active) Abort();
    }

    public void OnCommitted(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        switch (_state)
        {
            case TransactionState.Committed:
                SafeInvoke(callback);
                return;
            case TransactionState.Aborted:
                return;
            default:
                (_onCommitted ??= new List<Action>()).Add(callback);
                return;
        }
    }

    public void OnRolledBack(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        switch (_state)
        {
            case TransactionState.Aborted:
                SafeInvoke(callback);
                return;
            case TransactionState.Committed:
                return;
            default:
                (_onRolledBack ??= new List<Action>()).Add(callback);
                return;
        }
    }

    private static void FireHooks(List<Action>? hooks)
    {
        if (hooks == null) return;
        for (int i = 0; i < hooks.Count; i++) SafeInvoke(hooks[i]);
    }

    private static void SafeInvoke(Action callback)
    {
        // オブザーバーの例外でトランザクション結果を変えてはならない。
        try { callback(); } catch { }
    }

    private void ReleaseAllLocks()
    {
        _vertexLocks.ReleaseAll(Id);
        _edgeLocks.ReleaseAll(Id);
        _nexusLocks.ReleaseAll(Id);
        _indexLocks.ReleaseAll(Id);
    }
}
