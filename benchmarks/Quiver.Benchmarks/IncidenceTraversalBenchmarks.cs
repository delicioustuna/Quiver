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
        Console.WriteLine($"{"Degree",8} {"Binary p50",12} {"Node chain",12} {"Member expand",14} {"Chain p50",12} {"Block p50",12} {"Block ratio",12} {"Chain alloc",12} {"Block alloc",12}");
        Console.WriteLine(new string('-', 128));

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
            var coMembershipStore = new CoMembershipBlockStore(
                [(SubjectRole, ObjectRole)]);

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
            coMembershipStore.Rebuild(hyperedgeStore, incidenceStore);

            // warmup
            for (int i = 0; i < warmup; i++)
            {
                BinaryOneHop(relStore, hub, nodeStore);
                CoMembershipRoleFilter(incidenceStore, hub, nodeHeadStore, hyperedgeStore);
                CoMembershipBlock(coMembershipStore, hub, hyperedgeStore);
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
            allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                CoMembershipBlock(coMembershipStore, hub, hyperedgeStore);
            long blockAllocPer = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / 100;

            // measure co-membership latency
            var coMemTimes = new double[iterations];
            var blockTimes = new double[iterations];
            for (int i = 0; i < iterations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                int c = CoMembershipRoleFilter(incidenceStore, hub, nodeHeadStore, hyperedgeStore);
                coMemTimes[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                if (c != degree) throw new InvalidOperationException($"co-mem count {c} != {degree}");

                start = Stopwatch.GetTimestamp();
                c = CoMembershipBlock(coMembershipStore, hub, hyperedgeStore);
                blockTimes[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                if (c != degree) throw new InvalidOperationException($"block count {c} != {degree}");
            }

            var hyperedgeIds = new HyperedgeId[degree];
            int hyperedgeCount = CollectHyperedges(
                incidenceStore, hub, nodeHeadStore, hyperedgeStore, hyperedgeIds);
            if (hyperedgeCount != degree)
                throw new InvalidOperationException($"hyperedge count {hyperedgeCount} != {degree}");

            // 1-hop を構成する二段を分けて測る。配列化は計測外で行い、
            // 第一段は node chain の可視性判定まで、第二段は同じ header 集合から
            // role 指定 member を展開する費用だけを計上する。
            var nodeChainTimes = new double[iterations];
            var memberExpandTimes = new double[iterations];
            for (int i = 0; i < iterations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                int c = CountHyperedges(incidenceStore, hub, nodeHeadStore, hyperedgeStore);
                nodeChainTimes[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                if (c != degree) throw new InvalidOperationException($"node chain count {c} != {degree}");

                start = Stopwatch.GetTimestamp();
                c = ExpandMembers(incidenceStore, hyperedgeStore, hyperedgeIds);
                memberExpandTimes[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                if (c != degree) throw new InvalidOperationException($"member count {c} != {degree}");
            }

            Array.Sort(binaryTimes);
            Array.Sort(coMemTimes);
            Array.Sort(blockTimes);
            Array.Sort(nodeChainTimes);
            Array.Sort(memberExpandTimes);

            double bp50 = binaryTimes[(int)(iterations * 0.50)];
            double cp50 = coMemTimes[(int)(iterations * 0.50)];
            double vp50 = blockTimes[(int)(iterations * 0.50)];
            double np50 = nodeChainTimes[(int)(iterations * 0.50)];
            double mp50 = memberExpandTimes[(int)(iterations * 0.50)];
            double ratio = vp50 / bp50;

            Console.WriteLine($"{degree,8} {bp50,12:F0} {np50,12:F0} {mp50,14:F0} {cp50,12:F0} {vp50,12:F0} {ratio,11:F2}x {allocPer,12} {blockAllocPer,12}");
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

    private static int CountHyperedges(
        IncidenceStore incidenceStore, NodeId hub,
        NodeIncidenceHeadStore nodeHeadStore, VersionedHyperedgeStore hyperedgeStore)
    {
        int count = 0;
        var nodeEn = incidenceStore.EnumerateByNode(hub, nodeHeadStore, hyperedgeStore);
        while (nodeEn.MoveNext()) count++;
        return count;
    }

    private static int CollectHyperedges(
        IncidenceStore incidenceStore, NodeId hub,
        NodeIncidenceHeadStore nodeHeadStore, VersionedHyperedgeStore hyperedgeStore,
        Span<HyperedgeId> destination)
    {
        int count = 0;
        var nodeEn = incidenceStore.EnumerateByNode(hub, nodeHeadStore, hyperedgeStore);
        while (nodeEn.MoveNext())
            destination[count++] = nodeEn.Current.HyperedgeId;
        return count;
    }

    private static int ExpandMembers(
        IncidenceStore incidenceStore, VersionedHyperedgeStore hyperedgeStore,
        ReadOnlySpan<HyperedgeId> hyperedgeIds)
    {
        int count = 0;
        foreach (HyperedgeId hyperedgeId in hyperedgeIds)
        {
            var memberEn = incidenceStore.EnumerateByHyperedge(hyperedgeId, hyperedgeStore);
            while (memberEn.MoveNext())
            {
                if (memberEn.Current.RoleId == ObjectRole)
                    count++;
            }
        }
        return count;
    }

    private static int CoMembershipBlock(
        CoMembershipBlockStore store,
        NodeId origin,
        VersionedHyperedgeStore hyperedgeStore)
    {
        CoMembershipEntry[] entries = store.GetEntries(
            origin, SubjectRole, ObjectRole, out int entryCount);
        int count = 0;
        for (int i = 0; i < entryCount; i++)
        {
            using var header = hyperedgeStore.Read(entries[i].HyperedgeId);
            if (header.InUse)
                count++;
        }
        return count;
    }
}
