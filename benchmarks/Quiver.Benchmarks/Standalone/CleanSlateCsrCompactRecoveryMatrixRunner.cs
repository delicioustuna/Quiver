using System.Diagnostics;
using System.Runtime.InteropServices;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Standalone;

public static class CleanSlateCsrCompactRecoveryMatrixRunner
{
    private const string ScoreKey = "score";

    public static int Run()
    {
        Console.WriteLine("=== Clean-slate CSR compact recovery matrix ===");
        Console.WriteLine(
            $"machine={Environment.MachineName}, procs={Environment.ProcessorCount}, " +
            $"runtime={RuntimeInformation.FrameworkDescription}");
        Console.WriteLine();

        var phases = new[]
        {
            CompactAdjacencyPhase.AfterDescriptorInvalidated,
            CompactAdjacencyPhase.AfterRebuild,
            CompactAdjacencyPhase.AfterFinalDescriptorFlushed,
        };
        var tornModes = new[] { TornMode.None, TornMode.WalTailZero };

        string root = BenchTempDir.Create("clean_slate_csr_compact_recovery");
        Directory.CreateDirectory(root);
        int passed = 0;
        int failed = 0;

        try
        {
            foreach (var phase in phases)
            {
                foreach (var tornMode in tornModes)
                {
                    string caseDir = Path.Combine(root, phase + "_" + tornMode);
                    Directory.CreateDirectory(caseDir);
                    string path = Path.Combine(caseDir, "graph.quiver");

                    int exitCode = RunCrashChild(path, phase);
                    if (tornMode == TornMode.WalTailZero)
                        ZeroFillTail(path + "-wal", 16);

                    bool expectAdjacencyView = phase == CompactAdjacencyPhase.AfterFinalDescriptorFlushed;
                    bool ok = exitCode != 0 && ValidateRecovered(path, expectAdjacencyView);
                    if (ok) passed++;
                    else failed++;

                    Console.WriteLine(
                        $"case, phase={phase}, torn={tornMode}, child_exit={exitCode}, " +
                        $"expected_view={expectAdjacencyView}, result={(ok ? "PASS" : "FAIL")}");
                }
            }

            Console.WriteLine("csv,csr_compact_recovery_matrix,passed,failed,result");
            Console.WriteLine(
                $"csv,csr_compact_recovery_matrix,{passed},{failed},{(failed == 0 ? "PASS" : "FAIL")}");
            return failed == 0 ? 0 : 2;
        }
        finally
        {
            BenchTempDir.Delete(root);
        }
    }

    public static int RunChild(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
            return 2;

        string path = args[0];
        var killAt = Enum.Parse<CompactAdjacencyPhase>(args[1]);
        SeedCompactCase(path);

        using var db = QuiverDatabase.Open(path);
        BinaryGraphStorageBackend.CompactAdjacencyPhaseInjector = phase =>
        {
            if (phase == killAt)
                Environment.FailFast($"compact recovery matrix killed at {phase}");
        };

        db.CompactAdjacency();
        return 2;
    }

    private static int RunCrashChild(string path, CompactAdjacencyPhase phase)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            throw new InvalidOperationException("Current process path is unavailable.");

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--clean-slate-csr-compact-recovery-child");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(phase.ToString());
        start.Environment["QUIVER_BENCH_SKIP_SWEEP"] = "1";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start compact recovery child process.");
        process.WaitForExit(60_000);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        return process.ExitCode;
    }

    private static void SeedCompactCase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PropertyKeyId scoreKey;
        using (var db = QuiverDatabase.Open(path))
        {
            scoreKey = db.EditSchema(schema => schema.GetOrCreatePropertyKey(ScoreKey));
            using var loader = db.BeginBulkLoad(buildAdjacencyIndex: true);
            loader.WithPayloadLane(PayloadLaneSpec.ForInt64(scoreKey.Value));
            loader.AppendVertex(new VertexId(0), new LabelId(0));
            for (int i = 1; i <= 3; i++)
            {
                loader.AppendVertex(new VertexId(i), new LabelId(1));
                var edge = new EdgeId(i - 1);
                loader.AppendEdge(edge, new VertexId(0), new VertexId(i), new EdgeTypeId(0));
                loader.AppendEdgePayload(edge, scoreKey, 100 + i);
            }

            loader.Commit();
        }

        using (var db = QuiverDatabase.Open(path))
        using (var tx = db.BeginWriteTransaction())
        {
            tx.SetProperty(new EdgeId(0), ScoreKey, PropertyValue.FromInt64(700));
            var deltaVertex = tx.CreateVertex("V");
            var deltaEdge = tx.CreateEdge(new VertexId(0), deltaVertex, "LINK");
            tx.SetProperty(deltaEdge, ScoreKey, PropertyValue.FromInt64(900));
            tx.DeleteEdge(new EdgeId(2));
            tx.Commit();
        }
    }

    private static bool ValidateRecovered(string path, bool expectAdjacencyView)
    {
        using var db = QuiverDatabase.Open(path);
        using var tx = db.BeginReadTransaction();
        var adjacency = tx.AsInternal().AdjacencySegments;
        if (expectAdjacencyView)
        {
            if (adjacency is not IAdjacencyPayloadView)
                return false;

            var weights = ReadOutgoingWeights(tx, new VertexId(0));
            if (!weights.TryGetValue(1, out long first) || first != 700)
                return false;
            if (!weights.TryGetValue(2, out long second) || second != 102)
                return false;
            if (weights.ContainsKey(3))
                return false;
            if (!weights.Values.Contains(900))
                return false;
        }
        else if (adjacency is not null)
        {
            return false;
        }

        var targets = EnumerateOutgoingTargets(tx, new VertexId(0));
        return targets.Count == 3 &&
               targets.Contains(1) &&
               targets.Contains(2) &&
               targets.Any(static x => x > 3);
    }

    private static Dictionary<long, long> ReadOutgoingWeights(IReadTransaction tx, VertexId source)
    {
        var result = new Dictionary<long, long>();
        var adjacency = tx.AsInternal().AdjacencySegments
            ?? throw new InvalidOperationException("Adjacency block store is missing.");

        using var cursor = adjacency.OpenCursor(source, Direction.Outgoing, null);
        while (cursor.MoveNext())
        {
            if (!adjacency.IsTombstoned(cursor.Edge))
                result[cursor.Neighbor.Sequence] = cursor.WeightRaw;
        }

        return result;
    }

    private static List<long> EnumerateOutgoingTargets(IReadTransaction tx, VertexId source)
    {
        var result = new List<long>();
        var cursor = tx.EnumerateEdges(source, Direction.Outgoing);
        while (cursor.MoveNext())
            result.Add(cursor.Current.Target.Sequence);
        return result;
    }

    private static void ZeroFillTail(string path, int tailBytes)
    {
        if (!File.Exists(path))
            return;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long len = fs.Length;
        long start = Math.Max(0, len - tailBytes);
        fs.Position = start;
        Span<byte> zeros = stackalloc byte[Math.Min(4096, (int)(len - start))];
        long remaining = len - start;
        while (remaining > 0)
        {
            int n = (int)Math.Min(zeros.Length, remaining);
            fs.Write(zeros[..n]);
            remaining -= n;
        }

        fs.Flush(flushToDisk: true);
    }

    private enum TornMode
    {
        None,
        WalTailZero,
    }
}
