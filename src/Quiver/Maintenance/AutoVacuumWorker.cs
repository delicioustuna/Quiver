using System.Diagnostics;

namespace Quiver.Maintenance;

/// <summary>
/// <see cref="GraphDatabaseOptions.AutoVacuum"/> が有効なときに、周期的に
/// <see cref="IVacuum.Run"/> を起動する低頻度バックグラウンドワーカー。
/// </summary>
/// <remarks>
/// 設計:
/// <list type="bullet">
///   <item><see cref="System.Threading.Timer"/> 駆動。初回も 1 周期後に発火する
///   (DB open 直後に重い vacuum が走って起動レイテンシを悪化させないため)。</item>
///   <item>各 tick は <see cref="IVacuum.Run"/> を呼ぶだけ。アクティブ tx があれば
///   vacuum 自身が <see cref="VacuumReport.Skipped"/> = true で安全に no-op するので、
///   ワーカー側で tx 数を判定する必要はない。</item>
///   <item>tick は逐次実行 (re-entrancy ガード)。前回 tick がまだ走っている間に
///   次の周期が来ても二重起動しない。長時間 vacuum が周期を食い潰しても貯まらない。</item>
///   <item>vacuum 中の例外はワーカー内で握り潰す。バックグラウンドの失敗で本体 DB を
///   巻き込まない (<see cref="DeadlockDetector"/> と同じ方針)。</item>
///   <item><see cref="Dispose"/> は idempotent。進行中 tick の完了を最大
///   <see cref="StopJoinTimeout"/> まで待ってから戻る。</item>
/// </list>
/// </remarks>
internal sealed class AutoVacuumWorker : IDisposable
{
    /// <summary>Dispose 時に進行中 tick の完了を待つ上限。</summary>
    private static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<VacuumReport> _runVacuum;
    private readonly Timer _timer;
    // 0 = idle, 1 = tick 実行中。逐次実行のための re-entrancy ガード。
    private int _tickRunning;
    private int _disposed;
    private long _runCount;
    private long _skippedCount;

    /// <summary>実際に vacuum を起動した tick 累計回数 (Skipped 含む)。テスト / 診断用。</summary>
    public long RunCount => Interlocked.Read(ref _runCount);

    /// <summary>アクティブ tx 等で Skipped 扱いになった tick 累計回数。テスト / 診断用。</summary>
    public long SkippedCount => Interlocked.Read(ref _skippedCount);

    /// <summary>テスト用フック: 1 tick が終わるたびに発火 (周期駆動・手動 <see cref="RunOnce"/> 両方)。</summary>
    internal Action<VacuumReport>? OnTickCompleted;

    /// <param name="runVacuum">
    /// 1 tick で起動する vacuum 関数。通常は <c>() =&gt; graphDatabase.Vacuum()</c>。
    /// </param>
    /// <param name="interval">起動周期。<see cref="TimeSpan.Zero"/> 以下は不可。</param>
    public AutoVacuumWorker(Func<VacuumReport> runVacuum, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(runVacuum);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval,
                "AutoVacuum interval must be positive.");
        _runVacuum = runVacuum;
        // 初回も interval 後 (起動直後の vacuum 突入を避ける)。
        _timer = new Timer(_ => SafeTick(), null, interval, interval);
    }

    /// <summary>
    /// テストから即時に 1 tick を同期実行する。周期駆動と同じ re-entrancy ガード /
    /// 例外握り潰しを通る。進行中 tick があれば <c>null</c> を返してスキップ。
    /// </summary>
    internal VacuumReport? RunOnce() => SafeTick();

    private VacuumReport? SafeTick()
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        // 逐次実行: 既に tick が走っていれば今回はスキップ。
        if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0) return null;
        try
        {
            var report = _runVacuum();
            Interlocked.Increment(ref _runCount);
            if (report.Skipped) Interlocked.Increment(ref _skippedCount);
            OnTickCompleted?.Invoke(report);
            return report;
        }
        catch
        {
            // バックグラウンド vacuum の失敗で本体 DB を巻き込まない。
            return null;
        }
        finally
        {
            Volatile.Write(ref _tickRunning, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // 以降の周期発火を止める。進行中 tick はガード変数で完了を待つ。
        _timer.Dispose();

        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _tickRunning) != 0 && sw.Elapsed < StopJoinTimeout)
            Thread.Sleep(10);
    }
}
