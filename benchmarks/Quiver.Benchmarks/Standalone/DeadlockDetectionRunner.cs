using System.Diagnostics;
using Quiver.Core;
using Quiver.Transactions;

namespace Quiver.Benchmarks.Standalone;

/// <summary>
/// DeadlockDetector の特性を BDN 無しで短時間計測するランナー。
///
/// 計測軸:
///   1) detection latency — 意図的に作った 2-way circular wait が、検出器の周期に応じて
///      何 ms 以内に victim abort されるか。完了条件「500 ms 以内」の根拠データ。
///   2) CPU overhead — deadlock の無い高頻度 lock 取得ワークロードに対する detector の
///      throughput 影響。完了条件「1% 以内」の根拠データ。
///   3) cycle size scaling — 2 / 3 / 5 / 10 段の cycle 検出にかかる時間 (周期内 1 ラウンド)。
///
/// 起動方法: <c>dotnet run --project benchmarks/Quiver.Benchmarks -c Release -- --ft25-deadlock</c>
/// </summary>
public static class DeadlockDetectionRunner
{
    public static int Run()
    {
        Console.WriteLine("=== Deadlock Detection ===");
        RunDetectionLatency();
        Console.WriteLine();
        RunCpuOverhead();
        Console.WriteLine();
        RunCycleSizeScaling();
        return 0;
    }

    // ------------------------------------------------------------------------
    // 1) Detection latency
    // ------------------------------------------------------------------------
    private static void RunDetectionLatency()
    {
        Console.WriteLine("-- (1) Detection latency (2-way cycle, varying detector period) --");
        Console.WriteLine("period_ms, trials, latency_ms (median), latency_ms (p95), latency_ms (max)");

        int[] periods = { 25, 50, 100, 200, 500 };
        int trials = 20;

        foreach (var period in periods)
        {
            var latencies = new List<double>(trials);
            for (int i = 0; i < trials; i++)
            {
                latencies.Add(MeasureSingleDeadlockLatency(TimeSpan.FromMilliseconds(period)));
            }
            latencies.Sort();
            double median = latencies[latencies.Count / 2];
            double p95 = latencies[(int)(latencies.Count * 0.95)];
            double max = latencies[^1];
            Console.WriteLine($"{period},{trials},{median:F1},{p95:F1},{max:F1}");
        }
    }

    /// <summary>
    /// 2 tx を意図的に circular wait に陥らせ、detector が victim を中断するまでの
    /// wall-clock を返す (両 thread が abort / 完了するまで)。
    /// </summary>
    private static double MeasureSingleDeadlockLatency(TimeSpan period)
    {
        var lm = new LockManager();
        var tx1 = new TransactionId(1);
        var tx2 = new TransactionId(2);
        long e1 = 100, e2 = 200;

        // 初期所有
        lm.TryAcquire(e1, tx1, LockMode.Exclusive, TimeSpan.FromSeconds(10));
        lm.TryAcquire(e2, tx2, LockMode.Exclusive, TimeSpan.FromSeconds(10));

        var ready = new CountdownEvent(2);
        var t1 = new Thread(() =>
        {
            ready.Signal();
            try { lm.TryAcquire(e2, tx1, LockMode.Exclusive, TimeSpan.FromSeconds(10)); lm.ReleaseAll(tx1); }
            catch { lm.ReleaseAll(tx1); }
        }) { IsBackground = true };
        var t2 = new Thread(() =>
        {
            ready.Signal();
            try { lm.TryAcquire(e1, tx2, LockMode.Exclusive, TimeSpan.FromSeconds(10)); lm.ReleaseAll(tx2); }
            catch { lm.ReleaseAll(tx2); }
        }) { IsBackground = true };

        t1.Start(); t2.Start();
        ready.Wait();
        // 両 thread が Wait に入るまで小さな猶予
        Thread.Sleep(20);

        var sw = Stopwatch.StartNew();
        using (var detector = new DeadlockDetector(new[] { lm }, period))
        {
            t1.Join(); t2.Join();
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    // ------------------------------------------------------------------------
    // 2) CPU overhead (no-deadlock workload)
    // ------------------------------------------------------------------------
    private static void RunCpuOverhead()
    {
        Console.WriteLine("-- (2) CPU overhead (N threads, no deadlock, 3s) --");
        Console.WriteLine("threads, mode, acquires/sec, overhead_pct");

        int durationMs = 3_000;
        int[] threadCounts = { 1, 4, 16 };

        foreach (var n in threadCounts)
        {
            long baseline = MeasureHighFrequencyAcquire(n, durationMs, detectorPeriod: null);
            long withDetector = MeasureHighFrequencyAcquire(n, durationMs, detectorPeriod: TimeSpan.FromMilliseconds(100));
            double overheadPct = baseline == 0 ? 0 : (1.0 - (double)withDetector / baseline) * 100.0;
            Console.WriteLine($"{n},NoDetector,{baseline * 1000L / durationMs},0");
            Console.WriteLine($"{n},Detector100ms,{withDetector * 1000L / durationMs},{overheadPct:F2}");
        }
    }

    /// <summary>
    /// N thread が異なる entityId (= 競合せず) を取って即解放する high-frequency 経路で、
    /// detector の有無による throughput 差を測る。
    /// </summary>
    private static long MeasureHighFrequencyAcquire(int threadCount, int durationMs, TimeSpan? detectorPeriod)
    {
        var lm = new LockManager();
        var stop = new ManualResetEventSlim(false);
        var ready = new CountdownEvent(threadCount);
        long[] counts = new long[threadCount];
        var threads = new Thread[threadCount];
        bool stopFlag = false;

        for (int t = 0; t < threadCount; t++)
        {
            int local = t;
            threads[t] = new Thread(() =>
            {
                var tx = new TransactionId(local + 1);
                long entityId = local; // thread ごとに別 id (競合なし) → detector の SnapshotWaitEdges に
                                       // 入る waiter は常に 0 件 → overhead 純度が高い計測
                ready.Signal();
                stop.Wait();
                long c = 0;
                while (!Volatile.Read(ref stopFlag))
                {
                    if (lm.TryAcquire(entityId, tx, LockMode.Exclusive, TimeSpan.FromSeconds(1)))
                    {
                        lm.Release(entityId, tx);
                        c++;
                    }
                }
                counts[local] = c;
            }) { IsBackground = true };
            threads[t].Start();
        }

        DeadlockDetector? detector = null;
        if (detectorPeriod is { } p)
            detector = new DeadlockDetector(new[] { lm }, p);

        try
        {
            ready.Wait();
            stop.Set();
            Thread.Sleep(durationMs);
            Volatile.Write(ref stopFlag, true);
            foreach (var th in threads) th.Join();

            long total = 0;
            for (int i = 0; i < counts.Length; i++) total += counts[i];
            return total;
        }
        finally
        {
            detector?.Dispose();
        }
    }

    // ------------------------------------------------------------------------
    // 3) Cycle size scaling (single round of detector)
    // ------------------------------------------------------------------------
    private static void RunCycleSizeScaling()
    {
        Console.WriteLine("-- (3) Cycle size scaling (single RunOnce, N tx in single cycle) --");
        Console.WriteLine("cycle_size, snapshot+SCC_us (median over 100 runs)");

        int[] sizes = { 2, 3, 5, 10, 20 };
        foreach (var n in sizes)
        {
            var samples = new List<double>(100);
            for (int i = 0; i < 100; i++)
            {
                samples.Add(MeasureSingleRunOnce(n));
            }
            samples.Sort();
            double median = samples[samples.Count / 2];
            Console.WriteLine($"{n},{median:F2}");
        }
    }

    private static double MeasureSingleRunOnce(int cycleSize)
    {
        var lm = new LockManager();
        var txs = new TransactionId[cycleSize];
        for (int i = 0; i < cycleSize; i++)
        {
            txs[i] = new TransactionId(i + 1);
            lm.TryAcquire(i, txs[i], LockMode.Exclusive, TimeSpan.FromSeconds(10));
        }

        // tx_i → wait on entity_{(i+1) % n} (held by tx_{(i+1) % n})
        var ready = new CountdownEvent(cycleSize);
        var threads = new Thread[cycleSize];
        for (int i = 0; i < cycleSize; i++)
        {
            int local = i;
            threads[i] = new Thread(() =>
            {
                ready.Signal();
                try { lm.TryAcquire((local + 1) % cycleSize, txs[local], LockMode.Exclusive, TimeSpan.FromSeconds(10)); lm.ReleaseAll(txs[local]); }
                catch { lm.ReleaseAll(txs[local]); }
            }) { IsBackground = true };
            threads[i].Start();
        }
        ready.Wait();
        Thread.Sleep(20); // 全 thread が Wait に入るまで

        // 計測は RunOnce 単発のみ (= snapshot + SCC コスト)
        using var detector = new DeadlockDetector(new[] { lm }, TimeSpan.FromHours(1));
        var sw = Stopwatch.StartNew();
        detector.RunOnce();
        sw.Stop();

        // cleanup
        foreach (var th in threads) th.Join(TimeSpan.FromSeconds(5));
        return sw.Elapsed.TotalMicroseconds;
    }
}
