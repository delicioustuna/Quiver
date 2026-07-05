using System.Diagnostics;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks;

/// <summary>
/// incidence chain 走査と binary relationship 1-hop の store-level 比較。
/// role 指定 co-membership が binary 1-hop p50 の 3 倍以内かを判定する。
/// </summary>
public static class IncidenceTraversalBenchmarks
{
    private static readonly RoleId SubjectRole = new(1);
    private static readonly RoleId ObjectRole = new(2);
    private static readonly RoleId Extra1Role = new(3);
    private static readonly RoleId Extra2Role = new(4);

    public static void Run()
    {
        int[] degrees = [10, 100, 1_000];
        const int warmup = 500;
        const int iterations = 5_000;

        Console.WriteLine("=== Incidence traversal vs. binary 1-hop ===");
        Console.WriteLine($"warmup={warmup}  iterations={iterations}  arity=4");
        Console.WriteLine();
        Console.WriteLine($"{"Degree",8} {"Binary p50 (ns)",16} {"Binary p95 (ns)",16} {"CoMem p50 (ns)",16} {"CoMem p95 (ns)",16} {"Ratio p50",10} {"CoMem Alloc",12}");
        Console.WriteLine(new string('-', 98));

        foreach (int degree in degrees)
            RunDegree(degree, warmup, iterations);
    }

    private static void RunDegree(int degree, int warmup, int iterations)
    {
        string dbPath = BenchTempDir.Create("incidence");
        Directory.CreateDirectory(dbPath);

        var files = new PagedFile[11];
        for (int i = 0; i < files.Length; i++)
            files[i] = new PagedFile(Path.Combine(dbPath, $"f{i}"));

        try
        {
            var nodeStore = new VersionedNodeStore(files[0], new ItemPointerMap(files[1]), labelIndex: null);
            var relStore = new VersionedRelationshipStore(files[2], new ItemPointerMap(files[3]));
            var hyperedgeStore = new VersionedHyperedgeStore(files[4], new ItemPointerMap(files[5]));
            var incidenceStore = new IncidenceStore(files[6]);
            var nodeHeadStore = new NodeIncidenceHeadStore(files[8]);

            NodeId hub = nodeStore.Allocate(new LabelId(1));

            for (int i = 0; i < degree; i++)
            {
                var obj = nodeStore.Allocate(new LabelId(2));
                var extra1 = nodeStore.Allocate(new LabelId(2));
                var extra2 = nodeStore.Allocate(new LabelId(2));

                relStore.Create(nodeStore, hub, obj, new RelationshipTypeId(1));

                IncidenceMember[] members =
                [
                    new(hub, SubjectRole),
                    new(obj, ObjectRole),
                    new(extra1, Extra1Role),
                    new(extra2, Extra2Role),
                ];
                hyperedgeStore.Create(new HyperedgeTypeId(1), members, incidenceStore, nodeHeadStore);
            }

            // warmup
            for (int i = 0; i < warmup; i++)
            {
                BinaryOneHop(relStore, hub, nodeStore);
                CoMembershipRoleFilter(incidenceStore, hub, nodeHeadStore, hyperedgeStore);
            }

            // measure binary
            var binaryTimes = new double[iterations];
            for (int i = 0; i < iterations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                int c = BinaryOneHop(relStore, hub, nodeStore);
                binaryTimes[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                if (c != degree) throw new InvalidOperationException($"binary count {c} != {degree}");
            }

            // measure co-membership allocation
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                CoMembershipRoleFilter(incidenceStore, hub, nodeHeadStore, hyperedgeStore);
            long allocPer = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / 100;

            // measure co-membership latency
            var coMemTimes = new double[iterations];
            for (int i = 0; i < iterations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                int c = CoMembershipRoleFilter(incidenceStore, hub, nodeHeadStore, hyperedgeStore);
                coMemTimes[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                if (c != degree) throw new InvalidOperationException($"co-mem count {c} != {degree}");
            }

            Array.Sort(binaryTimes);
            Array.Sort(coMemTimes);

            double bp50 = binaryTimes[(int)(iterations * 0.50)];
            double bp95 = binaryTimes[(int)(iterations * 0.95)];
            double cp50 = coMemTimes[(int)(iterations * 0.50)];
            double cp95 = coMemTimes[(int)(iterations * 0.95)];
            double ratio = cp50 / bp50;

            Console.WriteLine($"{degree,8} {bp50,16:F0} {bp95,16:F0} {cp50,16:F0} {cp95,16:F0} {ratio,10:F2}x {allocPer,12}");
        }
        finally
        {
            foreach (var f in files)
                f.Dispose();
            BenchTempDir.Delete(dbPath);
        }
    }

    private static int BinaryOneHop(VersionedRelationshipStore relStore, NodeId hub, VersionedNodeStore nodeStore)
    {
        int count = 0;
        var en = relStore.EnumerateNeighbors(hub, nodeStore);
        while (en.MoveNext()) count++;
        return count;
    }

    private static int CoMembershipRoleFilter(
        IncidenceStore incidenceStore, NodeId hub,
        NodeIncidenceHeadStore nodeHeadStore, VersionedHyperedgeStore hyperedgeStore)
    {
        int count = 0;
        var nodeEn = incidenceStore.EnumerateByNode(hub, nodeHeadStore, hyperedgeStore);
        while (nodeEn.MoveNext())
        {
            HyperedgeId heId = nodeEn.Current.HyperedgeId;
            var memberEn = incidenceStore.EnumerateByHyperedge(heId, hyperedgeStore);
            while (memberEn.MoveNext())
            {
                if (memberEn.Current.RoleId == ObjectRole)
                    count++;
            }
        }
        return count;
    }
}
