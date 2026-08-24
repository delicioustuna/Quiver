using System.Diagnostics;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Benchmarks;

/// <summary>
/// incidence chain 走査と binary edge 1-hop の store-level 比較。
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
        Console.WriteLine($"{"Degree",8} {"Binary p50",12} {"Vertex chain",12} {"Member expand",14} {"Chain p50",12} {"Block p50",12} {"Block ratio",12} {"Chain alloc",12} {"Block alloc",12}");
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
            var vertexStore = new VersionedVertexStore(files[0], new ItemPointerMap(files[1]), labelIndex: null);
            var edgeStore = new VersionedEdgeStore(files[2], new ItemPointerMap(files[3]));
            var nexusStore = new VersionedNexusStore(files[4], new ItemPointerMap(files[5]));
            var incidenceStore = new IncidenceStore(files[6]);
            var vertexHeadStore = new VertexIncidenceHeadStore(files[8]);
            var coMembershipStore = new CoMembershipBlockStore(
                [(SubjectRole, ObjectRole)]);

            VertexId hub = vertexStore.Allocate(new LabelId(1));

            for (int i = 0; i < degree; i++)
            {
                var obj = vertexStore.Allocate(new LabelId(2));
                var extra1 = vertexStore.Allocate(new LabelId(2));
                var extra2 = vertexStore.Allocate(new LabelId(2));

                edgeStore.Create(vertexStore, hub, obj, new EdgeTypeId(1));

                IncidenceMember[] members =
                [
                    new(hub, SubjectRole),
                    new(obj, ObjectRole),
                    new(extra1, Extra1Role),
                    new(extra2, Extra2Role),
                ];
                nexusStore.Create(new NexusTypeId(1), members, incidenceStore, vertexHeadStore);
            }
            coMembershipStore.Rebuild(nexusStore, incidenceStore);

            // warmup
            for (int i = 0; i < warmup; i++)
            {
                BinaryOneHop(edgeStore, hub, vertexStore);
                CoMembershipRoleFilter(incidenceStore, hub, vertexHeadStore, nexusStore);
                CoMembershipBlock(coMembershipStore, hub, nexusStore);
            }

            // measure binary
            var binaryTimes = new double[iterations];
            for (int i = 0; i < iterations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                int c = BinaryOneHop(edgeStore, hub, vertexStore);
                binaryTimes[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                if (c != degree) throw new InvalidOperationException($"binary count {c} != {degree}");
            }

            // measure co-membership allocation
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                CoMembershipRoleFilter(incidenceStore, hub, vertexHeadStore, nexusStore);
            long allocPer = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / 100;
            allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                CoMembershipBlock(coMembershipStore, hub, nexusStore);
            long blockAllocPer = (GC.GetAllocatedBytesForCurrentThread() - allocBefore) / 100;

            // measure co-membership latency
            var coMemTimes = new double[iterations];
            var blockTimes = new double[iterations];
            for (int i = 0; i < iterations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                int c = CoMembershipRoleFilter(incidenceStore, hub, vertexHeadStore, nexusStore);
                coMemTimes[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                if (c != degree) throw new InvalidOperationException($"co-mem count {c} != {degree}");

                start = Stopwatch.GetTimestamp();
                c = CoMembershipBlock(coMembershipStore, hub, nexusStore);
                blockTimes[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                if (c != degree) throw new InvalidOperationException($"block count {c} != {degree}");
            }

            var nexusIds = new NexusId[degree];
            int nexusCount = CollectNexuses(
                incidenceStore, hub, vertexHeadStore, nexusStore, nexusIds);
            if (nexusCount != degree)
                throw new InvalidOperationException($"nexus count {nexusCount} != {degree}");

            // 1-hop を構成する二段を分けて測る。配列化は計測外で行い、
            // 第一段は vertex chain の可視性判定まで、第二段は同じ header 集合から
            // role 指定 member を展開する費用だけを計上する。
            var vertexChainTimes = new double[iterations];
            var memberExpandTimes = new double[iterations];
            for (int i = 0; i < iterations; i++)
            {
                long start = Stopwatch.GetTimestamp();
                int c = CountNexuses(incidenceStore, hub, vertexHeadStore, nexusStore);
                vertexChainTimes[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                if (c != degree) throw new InvalidOperationException($"vertex chain count {c} != {degree}");

                start = Stopwatch.GetTimestamp();
                c = ExpandMembers(incidenceStore, nexusStore, nexusIds);
                memberExpandTimes[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                if (c != degree) throw new InvalidOperationException($"member count {c} != {degree}");
            }

            Array.Sort(binaryTimes);
            Array.Sort(coMemTimes);
            Array.Sort(blockTimes);
            Array.Sort(vertexChainTimes);
            Array.Sort(memberExpandTimes);

            double bp50 = binaryTimes[(int)(iterations * 0.50)];
            double cp50 = coMemTimes[(int)(iterations * 0.50)];
            double vp50 = blockTimes[(int)(iterations * 0.50)];
            double np50 = vertexChainTimes[(int)(iterations * 0.50)];
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

    private static int BinaryOneHop(VersionedEdgeStore edgeStore, VertexId hub, VersionedVertexStore vertexStore)
    {
        int count = 0;
        var en = edgeStore.EnumerateNeighbors(hub, vertexStore);
        while (en.MoveNext()) count++;
        return count;
    }

    private static int CoMembershipRoleFilter(
        IncidenceStore incidenceStore, VertexId hub,
        VertexIncidenceHeadStore vertexHeadStore, VersionedNexusStore nexusStore)
    {
        int count = 0;
        var vertexEn = incidenceStore.EnumerateByVertex(hub, vertexHeadStore, nexusStore);
        while (vertexEn.MoveNext())
        {
            NexusId heId = vertexEn.Current.NexusId;
            var memberEn = incidenceStore.EnumerateByNexus(heId, nexusStore);
            while (memberEn.MoveNext())
            {
                if (memberEn.Current.RoleId == ObjectRole)
                    count++;
            }
        }
        return count;
    }

    private static int CountNexuses(
        IncidenceStore incidenceStore, VertexId hub,
        VertexIncidenceHeadStore vertexHeadStore, VersionedNexusStore nexusStore)
    {
        int count = 0;
        var vertexEn = incidenceStore.EnumerateByVertex(hub, vertexHeadStore, nexusStore);
        while (vertexEn.MoveNext()) count++;
        return count;
    }

    private static int CollectNexuses(
        IncidenceStore incidenceStore, VertexId hub,
        VertexIncidenceHeadStore vertexHeadStore, VersionedNexusStore nexusStore,
        Span<NexusId> destination)
    {
        int count = 0;
        var vertexEn = incidenceStore.EnumerateByVertex(hub, vertexHeadStore, nexusStore);
        while (vertexEn.MoveNext())
            destination[count++] = vertexEn.Current.NexusId;
        return count;
    }

    private static int ExpandMembers(
        IncidenceStore incidenceStore, VersionedNexusStore nexusStore,
        ReadOnlySpan<NexusId> nexusIds)
    {
        int count = 0;
        foreach (NexusId nexusId in nexusIds)
        {
            var memberEn = incidenceStore.EnumerateByNexus(nexusId, nexusStore);
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
        VertexId origin,
        VersionedNexusStore nexusStore)
    {
        CoMembershipEntry[] entries = store.GetEntries(
            origin, SubjectRole, ObjectRole, out int entryCount);
        int count = 0;
        for (int i = 0; i < entryCount; i++)
        {
            using var header = nexusStore.Read(entries[i].NexusId);
            if (header.InUse)
                count++;
        }
        return count;
    }
}
