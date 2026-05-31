using System.Diagnostics;
using Quiver.Core;
using Quiver.Core.Telemetry;
using Quiver.Index;
using Quiver.Stores;
using Quiver.Wal;

namespace Quiver.Transactions;

internal sealed class Transaction : ITransaction
{
    private readonly IWriteAheadLog _wal;
    private readonly LockManager _nodeLocks;
    private readonly LockManager _relLocks;
    private readonly LockManager _indexLocks;
    private readonly TransactionManager _manager;
    private readonly TxNodeStore _nodes;
    private readonly TxRelationshipStore _relationships;
    private readonly TxPropertyStore _properties;
    private readonly TxIndexManager _indexes;
    private readonly IAdjacencyBlockStore? _adjStore;
    private readonly IGraphAccessMethods _access;
    // FT-15: null でない場合、abort / コミット失敗時にキャプチャ済み before-image を
    // データファイルへ書き戻し、ストアメタを再ロードしてインプロセス undo を行う。
    // FT-19 以降: 索引 PagedFile も EnableWalLogging により before-image capture 対象なので、
    // 本ハンドラだけで data + index 両方の in-process abort が完結する (FT-20 で IndexUndoLog 撤去)。
    private readonly AbortUndoHandler? _undoHandler;
    private List<Action>? _onCommitted;
    private List<Action>? _onRolledBack;
    private TransactionState _state;

    // FT-23: savepoint 管理。SavepointId.Value (連番) → スタック深度 (= WalPageContext のバケット index)。
    // RollbackTo で巻き戻しても savepoint 自体は消費しないので、Value は同じレベルで再利用される。
    // ReleaseSavepoint または親 savepoint の Rollback/Release で初めて無効化される。
    private long _nextSavepointId;
    private List<(long Id, int Level)>? _savepoints;

    // FT-33: SSN (Serializable) のときのみ非 null。read/write hook が η/π を更新し、
    // Commit の pre-commit 検証 + post-commit スタンプ書き戻しで使う。
    private readonly SsnContext? _ssn;
    private readonly IEntityVersionStore? _nodeVersions;
    private readonly IEntityVersionStore? _relVersions;
    // FT-34: Begin 時の commit-stamp クロック (snapshot 下限)。読んだ版の v.sstamp を π に
    // 反映するかの判定に使う (詳細は TransactionManager.CurrentCommitStampClock)。
    private readonly long _ssnSnapshotCstamp;

    public TransactionId Id { get; }
    public IsolationLevel Level { get; }
    public long SnapshotLsn { get; }
    public TransactionState State => _state;

    public INodeStore Nodes => _nodes;
    public IRelationshipStore Relationships => _relationships;
    public IPropertyStore Properties => _properties;
    public IIndexManager Indexes => _indexes;
    public IAdjacencyBlockStore? AdjacencyBlocks => _adjStore;
    public IGraphAccessMethods Access => _access;

    internal Transaction(
        TransactionId id, IsolationLevel level, long snapshotLsn,
        IWriteAheadLog wal,
        LockManager nodeLocks, LockManager relLocks, LockManager indexLocks,
        TransactionManager manager,
        INodeStore nodeStore, IRelationshipStore relStore,
        IPropertyStore propStore, IIndexManager indexManager,
        IAdjacencyBlockStore? adjStore = null,
        IGraphAccessMethods? access = null,
        AbortUndoHandler? undoHandler = null,
        LockingMode lockingMode = LockingMode.ExclusiveOnly,
        TimeSpan? lockTimeout = null,
        SnapshotState snapshot = default,
        CommittedTxRegistry? committed = null,
        IEntityVersionStore? nodeVersions = null,
        IEntityVersionStore? relVersions = null)
    {
        Id = id; Level = level; SnapshotLsn = snapshotLsn;
        _wal = wal;
        _nodeLocks = nodeLocks; _relLocks = relLocks; _indexLocks = indexLocks;
        _manager = manager;
        _adjStore = adjStore;
        _access = access ?? InlineGraphAccessMethods.Instance;
        _undoHandler = undoHandler;
        _state = TransactionState.Active;
        var timeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        // FT-33: Serializable かつ MVCC コンテキストがあるときのみ SSN を起動する。
        // sidecar が無い (旧テスト経路など) 場合は SI と同じ挙動に縮退する。
        _nodeVersions = nodeVersions;
        _relVersions = relVersions;
        _ssn = (level == IsolationLevel.Serializable && committed != null
            && nodeVersions != null && relVersions != null)
            ? new SsnContext() : null;
        // FT-34: Serializable のときだけ Begin 時点の commit-stamp クロックを捕捉する
        // (ctor は TransactionManager.Begin の _snapshotGate 下で走るため一貫した下限)。
        _ssnSnapshotCstamp = _ssn != null ? manager.CurrentCommitStampClock : 0;
        // FT-26: per-tx ambient コンテキストを Tx wrapper にも持たせ、各操作直前に
        // MvccContext を再アクティベートする (同一スレッドで複数 tx 操作を交互に
        // 行う場合の thread-static の取り違えを防ぐ)。
        var snap = snapshot.ActiveAtBegin == null ? SnapshotState.Empty : snapshot;
        _nodes = new TxNodeStore(nodeStore, nodeLocks, id, lockingMode, timeout, snap, committed, _ssn);
        _relationships = new TxRelationshipStore(relStore, relLocks, id, _nodes, lockingMode, timeout, snap, committed, _ssn);
        _properties = new TxPropertyStore(propStore, id, snap, committed, _ssn);
        _indexes = new TxIndexManager(indexManager, indexLocks, id, timeout);
        WalPageContext.Begin(wal, id);
        // FT-26: MVCC ambient コンテキスト開始 (Tx wrapper を介さない経路のため)。
        // null なら旧テスト等の互換経路として MvccContext を起動しない (= Bootstrap fallback)。
        if (committed != null)
        {
            MvccContext.Begin(id, snap, committed, _ssn);
        }
    }

    public void Commit()
    {
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot commit: transaction is not Active.");
        _state = TransactionState.Preparing;
        // OB-1: span + duration histogram。AlwaysOnSampler が無い環境 (StartActivity が null) では
        // ActivitySource はコストゼロで Stopwatch のみ走る。
        using var activity = QuiverTelemetry.TransactionActivitySource.StartActivity(
            "tx.commit", ActivityKind.Internal);
        activity?.SetTag("quiver.tx.id", Id.Value);
        // OB-3: tx 境界に構造化スコープを通す。Logger 未設定時は null になり no-op。
        using var logScope = QuiverLog.BeginTxScope(QuiverLog.TransactionLogger, Id.Value, "Commit");
        var sw = Stopwatch.StartNew();
        try
        {
            // FT-33: Serializable のときは PageImage を WAL へ流す前に SSN の
            // exclusion-window 検証を行う。違反なら SerializabilityException を投げ、
            // 下の catch が in-place rollback + Abort を行う。検証を通ったら同じ
            // critical section で post-commit スタンプを sidecar に書き戻し、それも
            // 本 tx の PageImage として WAL に乗せて durable にする。
            if (_ssn != null) SsnValidateAndStamp();
            // 案C: UnpinDirty はページイメージをトランザクションバッファにコアレスするだけ。
            // ここで全 PageImage を WAL へ追記し、その後に Commit レコードを書く。
            // Commit を最後に書くことで、recovery はコミット済みトランザクションの
            // ページイメージのみを replay する。
            WalPageContext.FlushPending();
            long lsn = _wal.Append(WalRecordType.Commit, Id, ReadOnlySpan<byte>.Empty);
            _wal.FlushTo(lsn);
            WalPageContext.End();
            // FT-26: MVCC ambient コンテキスト終了 (これ以降このスレッドは
            // ベンチ / bulk loader 等の Bootstrap fallback 経路に戻る)。
            MvccContext.End();
            ReleaseAllLocks();
            _state = TransactionState.Committed;
            _manager.OnCommit(Id);
            QuiverTelemetry.TxCommitCount.Add(1);
            QuiverTelemetry.TxCommitDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            QuiverEventSource.Log.TxCommit();
            QuiverLog.TxCommitted(QuiverLog.TransactionLogger, Id.Value, sw.Elapsed.TotalMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            // Commit failed mid-way (e.g. WAL flush failure). Surface as rollback
            // so registered OnRolledBack hooks observe a consistent outcome.
            // FT-15: roll the page changes back in place before discarding the
            // context, then mark the transaction aborted on the WAL.
            try { RollBackInPlace(); } catch { }
            // FT-29: drain 前に自分の PageImage を coalesce バッファから除去 (最適化)。
            try { _wal.EvictCoalescedPageImagesFor(Id); } catch { }
            try { _wal.Append(WalRecordType.Abort, Id, ReadOnlySpan<byte>.Empty); } catch { }
            try { WalPageContext.End(); } catch { }
            try { MvccContext.End(); } catch { }
            try { ReleaseAllLocks(); } catch { }
            _state = TransactionState.Aborted;
            _manager.OnAbort(Id);
            QuiverTelemetry.TxAbortCount.Add(1);
            QuiverTelemetry.TxAbortDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            QuiverEventSource.Log.TxAbort();
            QuiverLog.TxCommitFailed(QuiverLog.TransactionLogger, Id.Value, ex.Message, ex);
            activity?.SetStatus(ActivityStatusCode.Error, "commit failed → rolled back");
            FireHooks(_onRolledBack);
            throw;
        }
        FireHooks(_onCommitted);
    }

    public void Abort()
    {
        if (_state is TransactionState.Committed or TransactionState.Aborted) return;
        // OB-1: abort span + duration。Commit と同じ ActivitySource を共有。
        using var activity = QuiverTelemetry.TransactionActivitySource.StartActivity(
            "tx.abort", ActivityKind.Internal);
        activity?.SetTag("quiver.tx.id", Id.Value);
        // OB-3: 明示 Abort も同じスコープキーを通す。
        using var logScope = QuiverLog.BeginTxScope(QuiverLog.TransactionLogger, Id.Value, "Abort");
        var sw = Stopwatch.StartNew();
        // FT-15: in-process undo — restore captured before-images to the data
        // files and reload page-backed store metadata, so discarded nodes /
        // edges / properties are invisible to subsequent transactions. Must run
        // before WalPageContext.End() drops the per-transaction before-image buffer.
        RollBackInPlace();
        // FT-29: 共有 coalesce バッファに残った自分の PageImage を破棄してから Abort を書く。
        // (Append(Abort) の drain で aborted tx の after-image が WAL に漏れるのを抑制する最適化。
        // 漏れても recovery で abortedTxs により skip されるため correctness には影響しない。)
        _wal.EvictCoalescedPageImagesFor(Id);
        _wal.Append(WalRecordType.Abort, Id, ReadOnlySpan<byte>.Empty);
        WalPageContext.End();
        MvccContext.End();
        ReleaseAllLocks();
        _state = TransactionState.Aborted;
        _manager.OnAbort(Id);
        QuiverTelemetry.TxAbortCount.Add(1);
        QuiverTelemetry.TxAbortDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        QuiverLog.TxAborted(QuiverLog.TransactionLogger, Id.Value, sw.Elapsed.TotalMilliseconds);
        FireHooks(_onRolledBack);
    }

    // FT-15: apply this transaction's captured before-images in place. No-op for
    // read-only transactions and for backends without an undo handler wired.
    private void RollBackInPlace()
    {
        if (_undoHandler == null) return;
        var beforeImages = WalPageContext.CurrentBeforeImagePayloads;
        if (beforeImages.Count == 0) return;
        _undoHandler.Undo(beforeImages);
    }

    // ==================== FT-33: SSN commit protocol ====================

    /// <summary>
    /// SSN (Wang et al. DaMoN'15) Algorithm 1 の commit 時検証 + post-commit スタンプ書き戻し。
    /// π/η は <see cref="TransactionManager"/> の大域 commit-stamp クロックで計算する
    /// (begin-order の TxId ではなく commit-order)。read/write hook が収集した read/write set を
    /// もとに、creator cstamp / reader pstamp / overwriter sstamp を畳み込んで exclusion window
    /// (π(T) &gt; η(T)) を判定する。検証 + 書き戻しは <see cref="TransactionManager.SsnCommitGate"/>
    /// 下で直列化し、並行 Serializable commit 間の version スタンプ read-modify-write を保護する。
    ///
    /// <para>read 捕捉は <see cref="ISsnReadSink"/> をストアの物理読み取り点 (NodeStore /
    /// RelationshipStore の Read・Scan) に挿しているため、直接 Read だけでなく traversal の隣接走査・
    /// scan・index seek 後のレコード読みも一律 read-set に入る (= rw-antidependency の取りこぼしなし)。</para>
    ///
    /// <para>仕様上の限界 (設計でスコープ外、index versioning / 別タスク前提): phantom protection は
    /// 対象外 — 述語に新規一致する行や隣接の増加 (= 既存バージョンの読みではない) は検出しない。
    /// lock は SSN と併存し撤去しない (将来別タスク)。</para>
    ///
    /// <para>実装上の割り切り (いずれも安全側 = false-abort 方向で、missed-anomaly は起こさない):
    /// (1) 競合粒度は Node / Relationship 単位で per-property ではない (同一ノードの別プロパティ同士も
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
            // FT-33 (④): commit-stamp 高水位を node sidecar ヘッダへ耐久化する。本 tx の
            // WalPageContext がまだ生きているので commit と同一 page-WAL 単位で永続化され、
            // 再起動後の Open でこの値からクロックを再開できる (旧/新 stamp 空間の混在を防ぐ)。
            // 候補は gate 下で単調増加するため最新書き込みが最高値。
            _nodeVersions!.WriteCommitStampHighWater(candidate);
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

            // FT-34: 最終 commit stamp = π(T)。これを後続 tx が CommitStampOf / version stamp
            // 経由で観測することで η/π 伝播が推移的になり、3-cycle 以上の dangerous structure も
            // 検出できる (fresh counter のままだと推移性が壊れる)。
            long cstamp = pi;
            _manager.SetCommitStamp(Id.Value, cstamp);

            // post-commit: 読んだバージョンに reader cstamp (π(T)) を、上書きしたバージョンに
            // overwriter cstamp (π(T)) を記録する。これらの書き込みは WalPageContext がまだ
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
        EntityKind.Node => _nodeVersions,
        EntityKind.Relationship => _relVersions,
        _ => null,
    };

    // ==================== FT-23: Savepoint ====================

    public SavepointId Savepoint(string? name = null)
    {
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot create savepoint: transaction is not Active.");
        int level = WalPageContext.PushSavepoint();
        if (level < 0)
        {
            // 書き込みコンテキストが無い (例: 読み取り専用 tx) — savepoint は no-op で良いが、
            // RollbackTo / Release の正当性チェックのため id だけは発行しておく。
            level = 0;
        }
        long id = ++_nextSavepointId;
        (_savepoints ??= new List<(long, int)>()).Add((id, level));
        return new SavepointId(id, name);
    }

    public void RollbackTo(SavepointId savepoint)
    {
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot rollback to savepoint: transaction is not Active.");
        int index = FindSavepointIndex(savepoint.Value);
        if (index < 0)
            throw new TransactionException($"Savepoint {savepoint} is not valid in this transaction.");

        int level = _savepoints![index].Level;
        // FT-23: 上位 savepoint も同時に無効化する (PostgreSQL / SQL 標準: ROLLBACK TO Sn は
        // Sn より新しい全ての savepoint も解放する)。Sn 自身は消費しない。
        _savepoints.RemoveRange(index + 1, _savepoints.Count - index - 1);

        var beforeImages = WalPageContext.RollbackToSavepoint(level);
        if (_undoHandler != null && beforeImages.Count > 0)
            _undoHandler.UndoPartial(beforeImages);
    }

    public void ReleaseSavepoint(SavepointId savepoint)
    {
        if (_state != TransactionState.Active)
            throw new TransactionException("Cannot release savepoint: transaction is not Active.");
        int index = FindSavepointIndex(savepoint.Value);
        if (index < 0)
            throw new TransactionException($"Savepoint {savepoint} is not valid in this transaction.");

        int level = _savepoints![index].Level;
        // Release した savepoint より新しい savepoint も同時に無効化する (SQL 標準準拠)。
        _savepoints.RemoveRange(index, _savepoints.Count - index);
        WalPageContext.ReleaseSavepoint(level);
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
        // Observer exceptions must not affect the transaction outcome.
        try { callback(); } catch { }
    }

    private void ReleaseAllLocks()
    {
        _nodeLocks.ReleaseAll(Id);
        _relLocks.ReleaseAll(Id);
        _indexLocks.ReleaseAll(Id);
    }
}
