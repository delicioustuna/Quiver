using System.Collections.Concurrent;

namespace Quiver.Core;

/// <summary>
/// MVCC visibility 判定で「ある TxId はコミット済みか?」を引くためのレジストリ。
///
/// <para>状態モデル:</para>
/// <list type="bullet">
///   <item><term>Active</term><description>TransactionManager 側で管理 (本クラス管轄外)</description></item>
///   <item><term>Committed</term><description>本クラスに含まれる</description></item>
///   <item><term>Aborted / Unknown</term><description>どこにも存在しない (= visibility 判定では「コミットしていない」扱い)</description></item>
/// </list>
///
/// <para>
/// visibility horizon (= 最古アクティブ tx の TxId) を下回ったコミット済みエントリは、
/// もう誰のスナップショットにも掛からないので prune できる (今はリストに残すが、
/// vacuum 経路でメモリ削減を行う)。
/// </para>
///
/// <para>
/// 起動時 (RecoveryManager.Recover 完了直後) に WAL を走査して committed TxId 集合を
/// 復元する必要がある。recovery で aborted / crashed と判明した TxId は登録しないため
/// 自然に visibility false になる。なお <see cref="TransactionId.Bootstrap"/> は常に committed として
/// 起動時に登録しておく必要がある (= ベンチ / bulk-load / recovery 経路の xmin として使われる)。
/// </para>
/// </summary>
internal sealed class CommittedTxRegistry
{
    // ConcurrentDictionary を HashSet 代わりに使う (Set はスレッドセーフ実装が無いため)。
    // Value は dummy。Key だけが意味を持つ。
    private readonly ConcurrentDictionary<long, byte> _committed = new();
    private long _maxObservedTxId;
    private long _recoveryHorizon;

    public CommittedTxRegistry()
    {
        // FT-26: bootstrap TxId は常に committed 扱い。
        _committed[TransactionId.Bootstrap.Value] = 0;
        _maxObservedTxId = TransactionId.Bootstrap.Value;
        _recoveryHorizon = TransactionId.Bootstrap.Value; // 既定: Bootstrap のみ presumed-committed
    }

    /// <summary>
    /// 「この値以下の TxId は WAL に無くとも presumed-committed として扱う」境界。
    ///
    /// <para>
    /// 根拠: checkpoint の atomicity により、checkpoint は active tx が 0 の瞬間にしか
    /// 打たれない。よって checkpoint 後の data file に残っている xmin は (a) Bootstrap,
    /// (b) 既コミット tx, (c) abort され before-image で巻き戻されたなら record そのものが
    /// 存在しない、のいずれか。WAL truncate で消えた古い commit レコードは復元できないが、
    /// 「truncate された区間 = checkpoint 済み = data file durable」なので、その区間より古い
    /// xmin は visibility 上 committed として扱って良い。
    /// </para>
    ///
    /// <para>
    /// recovery 経路 (RecoveryManager) で WAL 走査後に「現存する WAL 上の最古 TxId - 1」を
    /// 渡す。新規 DB では Bootstrap 据え置き。
    /// </para>
    /// </summary>
    public long RecoveryHorizon
    {
        get => Volatile.Read(ref _recoveryHorizon);
        set => Volatile.Write(ref _recoveryHorizon, value);
    }

    /// <summary>
    /// recovery で観測された最大 TxId。TransactionManager の _nextTxId の起点をここから
    /// 進めることで、再起動後の新規 tx が過去 commit 済み id と衝突しないようにする。
    /// </summary>
    public long MaxObservedTxId => Volatile.Read(ref _maxObservedTxId);

    /// <summary>recovery 経路で WAL を走査しながら呼ぶ。最大値モノトニック増加。</summary>
    public void RecordMaxObservedTxId(long observed)
    {
        long current = Volatile.Read(ref _maxObservedTxId);
        while (observed > current)
        {
            long prev = Interlocked.CompareExchange(ref _maxObservedTxId, observed, current);
            if (prev == current) return;
            current = prev;
        }
    }

    /// <summary>コミット時に呼ぶ。冪等。</summary>
    public void MarkCommitted(TransactionId txId) => _committed[txId.Value] = 0;

    /// <summary>
    /// 指定された TxId がコミット済みなら true。Active / Aborted / 未登録は false。
    /// visibility 判定の hot path で呼ばれる。
    ///
    /// <para>
    /// <c>txId &lt;= RecoveryHorizon</c> は registry に居なくとも presumed-committed。
    /// これにより WAL truncate で commit レコードが消えた古い tx も visible に保てる。
    /// </para>
    /// </summary>
    public bool IsCommitted(long txId)
    {
        if (txId <= Volatile.Read(ref _recoveryHorizon)) return true;
        return _committed.ContainsKey(txId);
    }

    /// <summary>テスト用: 登録済み件数。</summary>
    public int Count => _committed.Count;

    /// <summary>
    /// vacuum 連携で、visibility horizon を下回ったエントリを取り除くフック。
    /// Bootstrap TxId は保護する。
    /// </summary>
    public int PruneBelow(long horizonTxId)
    {
        int removed = 0;
        foreach (var key in _committed.Keys)
        {
            if (key == TransactionId.Bootstrap.Value) continue;
            if (key < horizonTxId)
            {
                if (_committed.TryRemove(key, out _)) removed++;
            }
        }
        return removed;
    }
}
