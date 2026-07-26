using System.Diagnostics;
using System.Numerics;
using Quiver.Api;
using Quiver.Core;

namespace Quiver.Benchmarks.Experimental.MathUseCases;

internal static class VectorNeighborGraphSpikeRunner
{
    private const int Dimensions = 384;
    private const int Neighbors = 16;
    private const int ClusterCount = 5;
    private const string IndexName = "neighbor_graph_vectors";
    private const string PropertyName = "embedding";
    private static readonly TimeSpan PublicLoopLimit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BatchLimit = TimeSpan.FromSeconds(10);
    private const long PublicLoopAllocationLimit = 250_000_000;
    private const long BatchAllocationLimit = 900_000_000;

    public static int Run()
    {
        var total = Stopwatch.StartNew();
        Console.WriteLine(
            "neighbor-graph-spike dimension=384 k=16 clusters=5 samples=3 warmup=1 " +
            "parallel=false benchmarkDotNet=false");

        var reports = new List<CaseReport>();
        CasePair small = RunPair(100, runPublicLoop: true);
        reports.AddRange(small.Reports);

        CaseReport? publicSmall = small.PublicLoop is { Completed: true } ? small.PublicLoop : null;
        bool publicLoopSafe = publicSmall is not null
            && publicSmall.GraphMilliseconds <= PublicLoopLimit.TotalMilliseconds
            && publicSmall.AllocatedBytes <= PublicLoopAllocationLimit;
        bool batchSafe = small.Batch is { Completed: true };

        CasePair medium = default;
        if (batchSafe && total.Elapsed < TimeSpan.FromSeconds(45))
        {
            bool runPublicMedium = publicLoopSafe
                && publicSmall!.GraphMilliseconds * 6.25 < PublicLoopLimit.TotalMilliseconds
                && publicSmall.AllocatedBytes * 6.25 < PublicLoopAllocationLimit;
            medium = RunPair(250, runPublicMedium);
            reports.AddRange(medium.Reports);
        }
        else
        {
            Console.WriteLine("neighbor-graph-skip n=250 reason=preceding-case-or-total-budget");
        }

        if (medium.Batch is { Completed: true } batchMedium
            && batchMedium.GraphMilliseconds * 16 < 8_000
            && batchMedium.AllocatedBytes * 4 < 500_000_000
            && total.Elapsed < TimeSpan.FromSeconds(45))
        {
            CasePair large = RunPair(1_000, runPublicLoop: false);
            reports.AddRange(large.Reports);
        }
        else
        {
            Console.WriteLine("neighbor-graph-skip n=1000 mode=batch reason=safety-projection-or-total-budget");
        }

        CaseReport? bestComparison = reports
            .Where(static report => report.Mode == "batch" && report.Completed)
            .Select(batch =>
            {
                CaseReport? baseline = reports.FirstOrDefault(candidate =>
                    candidate.N == batch.N && candidate.Mode == "public-loop" && candidate.Completed);
                return baseline is null ? null : batch with
                {
                    Speedup = baseline.GraphMilliseconds / batch.GraphMilliseconds,
                    AllocationReduction = baseline.AllocatedBytes == 0
                        ? 0
                        : 1 - batch.AllocatedBytes / (double)baseline.AllocatedBytes,
                };
            })
            .Where(static report => report is not null)
            .OrderByDescending(static report => report!.N)
            .FirstOrDefault();

        string decision = bestComparison switch
        {
            null => "NO-GO",
            { Speedup: >= 2 } => "GO",
            { AllocationReduction: >= 0.90 } => "GO",
            { Speedup: > 1 } => "CONDITIONAL",
            _ => "NO-GO",
        };
        Console.WriteLine(
            $"neighbor-graph-decision value={decision} exact=true maxMeasuredN={reports.Where(static r => r.Completed).Select(static r => r.N).DefaultIfEmpty(0).Max()} " +
            $"totalMs={total.Elapsed.TotalMilliseconds:F3}");
        return reports.Any(static report => !report.Completed) && reports.All(static report => !report.Completed)
            ? 1
            : 0;
    }

    private static CasePair RunPair(int n, bool runPublicLoop)
    {
        string directory = BenchTempDir.Create($"neighbor_graph_{n}");
        string path = Path.Combine(directory, "graph.quiver");
        try
        {
            float[][] sourceVectors = CreateVectors(n);
            using var database = QuiverDatabase.Open(path);
            database.EditSchema(schema =>
            {
                schema.GetOrCreatePropertyKey(PropertyName);
                schema.CreateIndex(new VectorIndexDefinition(
                    IndexName,
                    new PropertyTarget(PropertyOwnerKind.Vertex, PropertyName, "Point"),
                    Dimensions,
                    DistanceMetric.Euclidean));
            });

            var owners = new EntityRef[n];
            using (IWriteTransaction write = database.BeginWriteTransaction())
            {
                for (int i = 0; i < n; i++)
                {
                    VertexId vertex = write.CreateVertex("Point");
                    owners[i] = EntityRef.From(vertex);
                    write.SetVectorProperty(owners[i], PropertyName, sourceVectors[i]);
                }
                write.Commit();
            }

            using IReadTransaction read = database.BeginReadTransaction();
            CaseReport? publicReport = null;
            if (runPublicLoop)
            {
                publicReport = Measure(
                    n,
                    "public-loop",
                    () => BuildWithPublicSearch(read, owners, sourceVectors),
                    PublicLoopLimit,
                    PublicLoopAllocationLimit);
            }
            else
            {
                Console.WriteLine($"neighbor-graph-skip n={n} mode=public-loop reason=safety-policy");
            }

            CaseReport batchReport = Measure(
                n,
                "batch",
                () => BuildExactBatch(read, owners),
                BatchLimit,
                BatchAllocationLimit);

            if (publicReport is { Completed: true } baseline && batchReport.Completed)
            {
                if (!baseline.Components.AsSpan().SequenceEqual(batchReport.Components))
                    throw new InvalidOperationException($"component composition differs at N={n}");
                double speedup = baseline.GraphMilliseconds / batchReport.GraphMilliseconds;
                double reduction = baseline.AllocatedBytes == 0
                    ? 0
                    : 1 - batchReport.AllocatedBytes / (double)baseline.AllocatedBytes;
                Console.WriteLine(
                    $"neighbor-graph-comparison n={n} speedup={speedup:F2} " +
                    $"allocationReductionPct={reduction * 100:F2} componentsMatch=true");
            }

            var reports = new List<CaseReport>(2);
            if (publicReport is not null) reports.Add(publicReport);
            reports.Add(batchReport);
            return new(publicReport, batchReport, reports);
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    private static CaseReport Measure(
        int n,
        string mode,
        Func<NeighborGraph> build,
        TimeSpan timeLimit,
        long allocationLimit)
    {
        try
        {
            _ = build();
        }
        catch (ResourceLimitException exception)
        {
            Console.WriteLine($"neighbor-graph-case n={n} mode={mode} completed=false phase=warmup reason={exception.Message}");
            return CaseReport.Stopped(n, mode);
        }

        var samples = new List<CaseReport>(3);
        for (int sample = 0; sample < 3; sample++)
        {
            long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            long graphStart = Stopwatch.GetTimestamp();
            NeighborGraph graph;
            try
            {
                graph = build();
            }
            catch (ResourceLimitException exception)
            {
                Console.WriteLine($"neighbor-graph-case n={n} mode={mode} completed=false sample={sample + 1} reason={exception.Message}");
                return CaseReport.Stopped(n, mode);
            }
            double graphMilliseconds = Stopwatch.GetElapsedTime(graphStart).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            if (graphMilliseconds > timeLimit.TotalMilliseconds || allocated > allocationLimit)
            {
                Console.WriteLine(
                    $"neighbor-graph-case n={n} mode={mode} completed=false sample={sample + 1} " +
                    $"reason=post-limit graphMs={graphMilliseconds:F3} allocBytes={allocated}");
                return CaseReport.Stopped(n, mode);
            }

            long h0Start = Stopwatch.GetTimestamp();
            int[] components = ComputeComponents(n, graph.Edges);
            double h0Milliseconds = Stopwatch.GetElapsedTime(h0Start).TotalMilliseconds;
            ValidateGraph(n, ownersCount: n, graph, components);
            samples.Add(new(
                n,
                mode,
                true,
                graphMilliseconds,
                allocated,
                h0Milliseconds,
                graph.Edges.Length,
                components,
                Speedup: 0,
                AllocationReduction: 0));
        }

        CaseReport median = samples.OrderBy(static report => report.GraphMilliseconds).ElementAt(1);
        Console.WriteLine(
            $"neighbor-graph-case n={n} mode={mode} completed=true samples=3 " +
            $"graphMs={median.GraphMilliseconds:F3} allocBytes={median.AllocatedBytes} " +
            $"h0Ms={median.H0Milliseconds:F3} edges={median.EdgeCount} " +
            $"components={median.Components.Distinct().Count()} exact=true");
        return median;
    }

    private static NeighborGraph BuildWithPublicSearch(
        IReadTransaction read,
        IReadOnlyList<EntityRef> owners,
        IReadOnlyList<float[]> vectors)
    {
        var ownerToPoint = owners
            .Select(static (owner, point) => (owner, point))
            .ToDictionary(static pair => pair.owner, static pair => pair.point);
        var edgeMap = new Dictionary<long, NeighborEdge>(owners.Count * Neighbors);
        var stopwatch = Stopwatch.StartNew();
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var options = new VectorSearchOptions { EfSearch = 64 };
        for (int point = 0; point < owners.Count; point++)
        {
            using VectorSearchCursor cursor = read.KnnSearch(
                IndexName,
                vectors[point],
                Neighbors + 1,
                options);
            while (cursor.MoveNext())
            {
                VectorSearchResult hit = cursor.Current;
                if (!ownerToPoint.TryGetValue(hit.Owner, out int other))
                    throw new InvalidOperationException("search returned an owner outside the snapshot input");
                if (other == point) continue;
                AddEdge(edgeMap, point, other, -hit.Score);
            }
            ThrowIfOverLimit(stopwatch, allocationBefore, PublicLoopLimit, PublicLoopAllocationLimit);
        }
        return new(edgeMap.Values.ToArray());
    }

    private static NeighborGraph BuildExactBatch(
        IReadTransaction read,
        IReadOnlyList<EntityRef> owners)
    {
        var stopwatch = Stopwatch.StartNew();
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var vectors = new float[owners.Count][];
        for (int i = 0; i < owners.Count; i++)
        {
            vectors[i] = new float[Dimensions];
            if (!read.TryGetVectorProperty(owners[i], PropertyName, vectors[i]))
                throw new InvalidOperationException("snapshot vector could not be materialized");
        }

        int[] bestVertices = new int[checked(owners.Count * Neighbors)];
        float[] bestDistances = new float[bestVertices.Length];
        Array.Fill(bestVertices, int.MaxValue);
        Array.Fill(bestDistances, float.PositiveInfinity);

        for (int left = 0; left < vectors.Length; left++)
        {
            for (int right = left + 1; right < vectors.Length; right++)
            {
                float distance = SquaredEuclidean(vectors[left], vectors[right]);
                Offer(bestVertices, bestDistances, left, right, distance);
                Offer(bestVertices, bestDistances, right, left, distance);
            }
            ThrowIfOverLimit(stopwatch, allocationBefore, BatchLimit, BatchAllocationLimit);
        }

        var edgeMap = new Dictionary<long, NeighborEdge>(checked(owners.Count * Neighbors));
        for (int point = 0; point < owners.Count; point++)
        {
            int offset = point * Neighbors;
            for (int rank = 0; rank < Neighbors; rank++)
            {
                int other = bestVertices[offset + rank];
                if (other == int.MaxValue) continue;
                AddEdge(edgeMap, point, other, bestDistances[offset + rank]);
            }
        }
        return new(edgeMap.Values.ToArray());
    }

    private static void Offer(
        int[] vertices,
        float[] distances,
        int owner,
        int candidate,
        float distance)
    {
        int offset = owner * Neighbors;
        int position = offset + Neighbors - 1;
        if (distance > distances[position]
            || distance == distances[position] && candidate >= vertices[position])
            return;

        while (position > offset
            && (distance < distances[position - 1]
                || distance == distances[position - 1] && candidate < vertices[position - 1]))
        {
            distances[position] = distances[position - 1];
            vertices[position] = vertices[position - 1];
            position--;
        }
        distances[position] = distance;
        vertices[position] = candidate;
    }

    private static float SquaredEuclidean(float[] left, float[] right)
    {
        int width = Vector<float>.Count;
        int i = 0;
        Vector<float> sum = Vector<float>.Zero;
        for (; i <= left.Length - width; i += width)
        {
            var delta = new Vector<float>(left, i) - new Vector<float>(right, i);
            sum += delta * delta;
        }
        float distance = Vector.Sum(sum);
        for (; i < left.Length; i++)
        {
            float delta = left[i] - right[i];
            distance += delta * delta;
        }
        return distance;
    }

    private static void AddEdge(
        IDictionary<long, NeighborEdge> edges,
        int left,
        int right,
        float distance)
    {
        int lo = Math.Min(left, right);
        int hi = Math.Max(left, right);
        long key = ((long)lo << 32) | (uint)hi;
        var candidate = new NeighborEdge(lo, hi, distance);
        if (!edges.TryGetValue(key, out NeighborEdge current) || distance < current.Distance)
            edges[key] = candidate;
    }

    private static int[] ComputeComponents(int n, NeighborEdge[] sourceEdges)
    {
        NeighborEdge[] edges = sourceEdges.OrderBy(static edge => edge.Distance).ToArray();
        int[] parent = Enumerable.Range(0, n).ToArray();
        foreach (NeighborEdge edge in edges)
        {
            int left = Find(parent, edge.Left);
            int right = Find(parent, edge.Right);
            if (left != right) parent[right] = left;
        }

        var canonical = new Dictionary<int, int>();
        var components = new int[n];
        for (int i = 0; i < n; i++)
        {
            int root = Find(parent, i);
            if (!canonical.TryGetValue(root, out int component))
            {
                component = canonical.Count;
                canonical.Add(root, component);
            }
            components[i] = component;
        }
        return components;
    }

    private static int Find(int[] parent, int value)
    {
        while (parent[value] != value)
        {
            parent[value] = parent[parent[value]];
            value = parent[value];
        }
        return value;
    }

    private static void ValidateGraph(
        int n,
        int ownersCount,
        NeighborGraph graph,
        int[] components)
    {
        var keys = new HashSet<long>();
        foreach (NeighborEdge edge in graph.Edges)
        {
            if (edge.Left == edge.Right)
                throw new InvalidOperationException("self edge detected");
            if ((uint)edge.Left >= (uint)ownersCount || (uint)edge.Right >= (uint)ownersCount)
                throw new InvalidOperationException("edge owner is outside the snapshot input");
            if (!float.IsFinite(edge.Distance))
                throw new InvalidOperationException("non-finite distance detected");
            long key = ((long)edge.Left << 32) | (uint)edge.Right;
            if (!keys.Add(key))
                throw new InvalidOperationException("duplicate undirected edge detected");
        }
        if (components.Length != n || components.Distinct().Count() != ClusterCount)
            throw new InvalidOperationException("planted component count was not recovered");
    }

    private static void ThrowIfOverLimit(
        Stopwatch stopwatch,
        long allocationBefore,
        TimeSpan timeLimit,
        long allocationLimit)
    {
        if (stopwatch.Elapsed > timeLimit)
            throw new ResourceLimitException("time-limit");
        if (GC.GetAllocatedBytesForCurrentThread() - allocationBefore > allocationLimit)
            throw new ResourceLimitException("allocation-limit");
    }

    private static float[][] CreateVectors(int count)
    {
        var random = new Random(0x4E474250 + count);
        var vectors = new float[count][];
        for (int point = 0; point < count; point++)
        {
            int cluster = point % ClusterCount;
            var vector = new float[Dimensions];
            vector[0] = cluster * 40;
            vector[1] = (cluster & 1) * 25;
            for (int dimension = 0; dimension < Dimensions; dimension++)
                vector[dimension] += (float)(NextGaussian(random) * 0.08);
            vectors[point] = vector;
        }
        return vectors;
    }

    private static double NextGaussian(Random random)
    {
        double u1 = 1 - random.NextDouble();
        double u2 = 1 - random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private sealed class ResourceLimitException(string message) : Exception(message);

    private sealed record NeighborGraph(NeighborEdge[] Edges);

    private readonly record struct NeighborEdge(int Left, int Right, float Distance);

    private sealed record CaseReport(
        int N,
        string Mode,
        bool Completed,
        double GraphMilliseconds,
        long AllocatedBytes,
        double H0Milliseconds,
        int EdgeCount,
        int[] Components,
        double Speedup,
        double AllocationReduction)
    {
        internal static CaseReport Stopped(int n, string mode)
            => new(n, mode, false, 0, 0, 0, 0, [], 0, 0);
    }

    private readonly record struct CasePair(
        CaseReport? PublicLoop,
        CaseReport? Batch,
        IReadOnlyList<CaseReport> Reports);
}
