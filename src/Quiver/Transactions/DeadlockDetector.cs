using Quiver.Core;
using Quiver.Telemetry;

namespace Quiver.Transactions;

/// <summary>
/// FT-25: 周期的に <see cref="LockManager"/> 群の wait-for graph を取得し、SCC で閉路を検出する。
/// 閉路内で最も若い tx (TxId.Value が最大) を犠牲者として選び、<see cref="LockManager.TryAbortWaiter"/>
/// 経由で <see cref="DeadlockException"/> を投げさせる。複数 lock manager (node / rel / index) を
/// またぐ deadlock も検出可能。
/// </summary>
internal sealed class DeadlockDetector : IDisposable
{
    private readonly LockManager[] _lockManagers;
    private readonly Timer _timer;
    private long _detectedCount;
    private int _disposed;

    /// <summary>
    /// 検出して犠牲者を中断した累計回数。<see cref="Quiver.IDiagnosticsApi"/> のメトリクスに公開予定 (OB-2)。
    /// </summary>
    public long DetectedCount => Interlocked.Read(ref _detectedCount);

    /// <summary>テスト用フック: 1 ラウンドが終わるたびに発火 (周期駆動と手動 RunOnce 両方)。</summary>
    internal Action? OnRoundCompleted;

    public DeadlockDetector(IEnumerable<LockManager> managers, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(managers);
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        _lockManagers = managers.ToArray();
        // 初回は period 後。CPU を node 化中に焼かないため周期と同じ delay。
        _timer = new Timer(_ => SafeRunOnce(), null, period, period);
    }

    /// <summary>テストから即時実行するための同期エントリ。</summary>
    internal int RunOnce()
    {
        var edges = new List<(TransactionId Waiter, TransactionId Holder)>();
        foreach (var lm in _lockManagers) lm.SnapshotWaitEdges(edges);

        int aborted = 0;
        if (edges.Count > 0)
        {
            var cycles = FindCycles(edges);
            foreach (var cycle in cycles)
            {
                if (cycle.Count < 2) continue; // 自己ループは Snapshot 側で除外済みだが念のため
                // youngest = TxId.Value が最大。
                TransactionId victim = cycle[0];
                for (int i = 1; i < cycle.Count; i++)
                    if (cycle[i].Value > victim.Value) victim = cycle[i];

                foreach (var lm in _lockManagers)
                {
                    if (lm.TryAbortWaiter(victim))
                    {
                        Interlocked.Increment(ref _detectedCount);
                        // OB-2: dotnet-counters の tx-deadlock-victim-count に反映。
                        QuiverEventSource.Log.DeadlockVictim();
                        aborted++;
                        break;
                    }
                }
            }
        }
        OnRoundCompleted?.Invoke();
        return aborted;
    }

    private void SafeRunOnce()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { RunOnce(); }
        catch { /* detector の失敗で本体停止させない */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
    }

    // --------------------- Tarjan SCC ---------------------
    // wait-for graph は通常極端に疎 (せいぜい数百ノード) なので Dictionary ベースで十分。
    // SCC のうち size >= 2 のものを「閉路」として返す。

    private static List<List<TransactionId>> FindCycles(List<(TransactionId Waiter, TransactionId Holder)> edges)
    {
        var adj = new Dictionary<TransactionId, List<TransactionId>>();
        foreach (var (f, t) in edges)
        {
            if (!adj.TryGetValue(f, out var list))
            {
                list = new List<TransactionId>();
                adj[f] = list;
            }
            list.Add(t);
            // Holder 単独 (送出 edge なし) でもノードとして登録しておく
            if (!adj.ContainsKey(t)) adj[t] = new List<TransactionId>();
        }

        var index = new Dictionary<TransactionId, int>();
        var lowlink = new Dictionary<TransactionId, int>();
        var onStack = new HashSet<TransactionId>();
        var stack = new Stack<TransactionId>();
        var sccs = new List<List<TransactionId>>();
        int nextIndex = 0;

        foreach (var v in adj.Keys)
        {
            if (!index.ContainsKey(v))
                StrongConnect(v, adj, index, lowlink, onStack, stack, sccs, ref nextIndex);
        }

        // size >= 2 のみ閉路として扱う (single node SCC = 閉路ではない)。
        var cycles = new List<List<TransactionId>>();
        foreach (var scc in sccs)
            if (scc.Count >= 2) cycles.Add(scc);
        return cycles;
    }

    private static void StrongConnect(
        TransactionId v,
        Dictionary<TransactionId, List<TransactionId>> adj,
        Dictionary<TransactionId, int> index,
        Dictionary<TransactionId, int> lowlink,
        HashSet<TransactionId> onStack,
        Stack<TransactionId> stack,
        List<List<TransactionId>> sccs,
        ref int nextIndex)
    {
        // 再帰版。tx 数は最大でも active count 程度なのでスタックオーバーフロー懸念は薄い。
        index[v] = nextIndex;
        lowlink[v] = nextIndex;
        nextIndex++;
        stack.Push(v);
        onStack.Add(v);

        foreach (var w in adj[v])
        {
            if (!index.ContainsKey(w))
            {
                StrongConnect(w, adj, index, lowlink, onStack, stack, sccs, ref nextIndex);
                lowlink[v] = Math.Min(lowlink[v], lowlink[w]);
            }
            else if (onStack.Contains(w))
            {
                lowlink[v] = Math.Min(lowlink[v], index[w]);
            }
        }

        if (lowlink[v] == index[v])
        {
            var scc = new List<TransactionId>();
            TransactionId w;
            do
            {
                w = stack.Pop();
                onStack.Remove(w);
                scc.Add(w);
            } while (w != v);
            sccs.Add(scc);
        }
    }
}
