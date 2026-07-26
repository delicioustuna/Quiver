using Quiver.Core;

namespace Quiver.Benchmarks.Experimental.MathUseCases;

internal static class DirectedHypergraphExperiments
{
    public static void Run()
    {
        Console.WriteLine("section=directed-hypergraph");
        VerifyCounterAgainstFixedPoint();
        DemonstrateOrdinaryReificationFailure();
        MeasureLinearReachability();
        VerifyCurrentNexusBoundary();
        DistinguishDerivationObjectives();
    }

    private static void VerifyCounterAgainstFixedPoint()
    {
        var random = new Random(0x42524541);
        for (int trial = 0; trial < 40; trial++)
        {
            int vertexCount = 6 + random.Next(8);
            int edgeCount = 4 + random.Next(14);
            var arcs = new GeneralArc[edgeCount];
            for (int edge = 0; edge < edgeCount; edge++)
            {
                int tailCount = 1 + random.Next(3);
                int[] tail = Enumerable.Range(0, vertexCount)
                    .OrderBy(_ => random.Next())
                    .Take(tailCount)
                    .ToArray();
                arcs[edge] = new GeneralArc(tail, random.Next(vertexCount), 1.0);
            }

            int[] seeds = Enumerable.Range(0, vertexCount)
                .Where(_ => random.NextDouble() < 0.25)
                .DefaultIfEmpty(0)
                .ToArray();

            bool[] expected = FixedPointReachability(vertexCount, arcs, seeds);
            bool[] actual = CounterReachability(vertexCount, arcs, seeds);
            SpikeCheck.SequenceEqual(expected, actual, "counter reachability disagreed with fixed-point oracle");
        }

        Console.WriteLine("directed.oracle=fixed-point trials=40 result=PASS");
    }

    private static void DemonstrateOrdinaryReificationFailure()
    {
        var arcs = new[] { new GeneralArc([0, 1], 2, 1.0) };
        bool[] expected = CounterReachability(3, arcs, [0]);
        bool[] reified = OrdinaryReifiedReachability(3, arcs, [0]);
        SpikeCheck.True(!expected[2], "AND reachability unexpectedly accepted a partial tail");
        SpikeCheck.True(reified[2], "ordinary reification did not expose its expected false positive");
        Console.WriteLine("directed.binary-reification=false-positive result=CONFIRMED");
    }

    private static void MeasureLinearReachability()
    {
        int[] edgeCounts = [10_000, 100_000, 1_000_000];
        var perIncidence = new List<double>(edgeCounts.Length);

        foreach (int edgeCount in edgeCounts)
        {
            var graph = TripleTailChain.Create(edgeCount);
            int repetitions = edgeCount switch
            {
                <= 10_000 => 50,
                <= 100_000 => 10,
                _ => 3,
            };
            AlternatingSamples samples = SpikeMeasurement.Alternate(
                () => SpikeMeasurement.Repeat(graph.ReachableCount, repetitions),
                () => SpikeMeasurement.Repeat(graph.ReachableCount, repetitions),
                warmup: 1,
                samples: 7);
            SpikeCheck.Equal(edgeCount + 3L, samples.First.Checksum, "scale run reached an unexpected vertex count");

            double nanosecondsPerIncidence =
                samples.First.MedianMilliseconds * 1_000_000.0 / (edgeCount * 3.0 * repetitions);
            perIncidence.Add(nanosecondsPerIncidence);
            Console.WriteLine(
                $"directed.scale edges={edgeCount} incidences={edgeCount * 3L} " +
                $"medianMs={samples.First.MedianMilliseconds / repetitions:F3} iqrPct={samples.First.IqrPercent:F2} " +
                $"allocBytes={samples.First.AllocatedBytes / repetitions} " +
                $"nsPerIncidence={nanosecondsPerIncidence:F2}");
        }

        double slopeSpread = perIncidence.Max() / perIncidence.Min();
        SpikeCheck.True(slopeSpread <= 3.0,
            $"reachability slope spread was superlinear: {slopeSpread:F2}x");
        Console.WriteLine($"directed.linearity slopeSpread={slopeSpread:F2}x result=PASS");
    }

    private static void VerifyCurrentNexusBoundary()
    {
        string directory = BenchTempDir.Create("math_directed_nexus");
        string path = Path.Combine(directory, "graph.quiver");
        try
        {
            using var db = QuiverDatabase.Open(path);
            VertexId[] vertices;
            using (var write = db.BeginWriteTransaction())
            {
                vertices = new VertexId[67];
                for (int i = 0; i < vertices.Length; i++)
                    vertices[i] = write.CreateVertex("Substance");

                for (int i = 0; i < 64; i++)
                {
                    write.CreateNexus("Reaction",
                    [
                        new NexusMember("tail", vertices[0]),
                        new NexusMember("tail", vertices[1]),
                        new NexusMember("tail", vertices[i + 2]),
                        new NexusMember("head", vertices[i + 3]),
                    ]);
                }
                write.Commit();
            }

            using var snapshot = db.BeginReadTransaction();
            int before = ReachableThroughNexuses(snapshot, [vertices[0], vertices[1], vertices[2]]);
            SpikeCheck.Equal(vertices.Length, before, "Nexus incidence adapter missed reachable vertices");

            VertexId late;
            using (var write = db.BeginWriteTransaction())
            {
                late = write.CreateVertex("Substance");
                write.CreateNexus("Reaction",
                [
                    new NexusMember("tail", vertices[^1]),
                    new NexusMember("head", late),
                ]);
                write.Commit();
            }

            int oldSnapshot = ReachableThroughNexuses(snapshot, [vertices[0], vertices[1], vertices[2]]);
            using var current = db.BeginReadTransaction();
            int newSnapshot = ReachableThroughNexuses(current, [vertices[0], vertices[1], vertices[2]]);
            SpikeCheck.Equal(before, oldSnapshot, "existing snapshot observed a newly committed Nexus");
            SpikeCheck.Equal(before + 1, newSnapshot, "new snapshot did not observe a committed Nexus");
            Console.WriteLine("directed.nexus-adapter roles=tail/head snapshotIsolation=PASS result=PASS");
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    private static void DistinguishDerivationObjectives()
    {
        GeneralArc[] arcs =
        [
            new([0], 1, 10),
            new([1], 2, 0),
            new([1], 3, 0),
            new([2, 3], 4, 0),
            new([0], 4, 15),
        ];

        double additiveTree = ShortestTreeCost(5, arcs, [0], 4, bottleneck: false);
        double bottleneck = ShortestTreeCost(5, arcs, [0], 4, bottleneck: true);
        double uniqueSubgraph = ExhaustiveUniqueArcCost(5, arcs, [0], 4);

        SpikeCheck.Equal(15.0, additiveTree, "unexpected additive derivation-tree cost");
        SpikeCheck.Equal(10.0, bottleneck, "unexpected bottleneck derivation cost");
        SpikeCheck.Equal(10.0, uniqueSubgraph, "unexpected shared derivation-subgraph cost");
        Console.WriteLine(
            $"directed.shortest additiveTree={additiveTree:F0} bottleneck={bottleneck:F0} " +
            $"sharedSubgraph={uniqueSubgraph:F0} contractAmbiguity=CONFIRMED");
    }

    private static bool[] CounterReachability(int vertexCount, GeneralArc[] arcs, int[] seeds)
    {
        var byTail = new List<int>[vertexCount];
        for (int vertex = 0; vertex < vertexCount; vertex++)
            byTail[vertex] = [];
        for (int edge = 0; edge < arcs.Length; edge++)
            foreach (int tail in arcs[edge].Tail.Distinct())
                byTail[tail].Add(edge);

        int[] remaining = arcs.Select(static edge => edge.Tail.Distinct().Count()).ToArray();
        var reached = new bool[vertexCount];
        var queue = new Queue<int>();
        foreach (int seed in seeds)
        {
            if (reached[seed]) continue;
            reached[seed] = true;
            queue.Enqueue(seed);
        }

        while (queue.TryDequeue(out int vertex))
        {
            foreach (int edge in byTail[vertex])
            {
                if (--remaining[edge] != 0) continue;
                int head = arcs[edge].Head;
                if (reached[head]) continue;
                reached[head] = true;
                queue.Enqueue(head);
            }
        }
        return reached;
    }

    private static bool[] FixedPointReachability(int vertexCount, GeneralArc[] arcs, int[] seeds)
    {
        var reached = new bool[vertexCount];
        foreach (int seed in seeds)
            reached[seed] = true;

        bool changed;
        do
        {
            changed = false;
            foreach (GeneralArc arc in arcs)
            {
                if (reached[arc.Head] || !arc.Tail.All(tail => reached[tail])) continue;
                reached[arc.Head] = true;
                changed = true;
            }
        }
        while (changed);
        return reached;
    }

    private static bool[] OrdinaryReifiedReachability(int vertexCount, GeneralArc[] arcs, int[] seeds)
    {
        var adjacency = new List<int>[vertexCount + arcs.Length];
        for (int i = 0; i < adjacency.Length; i++) adjacency[i] = [];
        for (int edge = 0; edge < arcs.Length; edge++)
        {
            int gate = vertexCount + edge;
            foreach (int tail in arcs[edge].Tail) adjacency[tail].Add(gate);
            adjacency[gate].Add(arcs[edge].Head);
        }

        var visited = new bool[adjacency.Length];
        var queue = new Queue<int>();
        foreach (int seed in seeds)
        {
            visited[seed] = true;
            queue.Enqueue(seed);
        }
        while (queue.TryDequeue(out int vertex))
        {
            foreach (int next in adjacency[vertex])
            {
                if (visited[next]) continue;
                visited[next] = true;
                queue.Enqueue(next);
            }
        }
        return visited[..vertexCount];
    }

    private static int ReachableThroughNexuses(IReadTransaction transaction, VertexId[] seeds)
    {
        var reached = new HashSet<VertexId>();
        var queue = new Queue<VertexId>();
        var remaining = new Dictionary<NexusId, int>();
        var heads = new Dictionary<NexusId, VertexId[]>();
        foreach (VertexId seed in seeds)
        {
            if (!reached.Add(seed)) continue;
            queue.Enqueue(seed);
        }

        while (queue.TryDequeue(out VertexId vertex))
        {
            var nexuses = transaction.GetNexuses(vertex, "Reaction", "tail");
            while (nexuses.MoveNext())
            {
                NexusId nexus = nexuses.Current;
                if (!remaining.TryGetValue(nexus, out int count))
                {
                    var tailSet = new HashSet<VertexId>();
                    var headList = new List<VertexId>();
                    var members = transaction.GetMembers(nexus);
                    while (members.MoveNext())
                    {
                        NexusMember member = members.Current;
                        if (member.Role == "tail") tailSet.Add(member.VertexId);
                        if (member.Role == "head") headList.Add(member.VertexId);
                    }
                    members.Dispose();
                    count = tailSet.Count;
                    heads[nexus] = headList.ToArray();
                }

                count--;
                remaining[nexus] = count;
                if (count != 0) continue;
                foreach (VertexId head in heads[nexus])
                {
                    if (!reached.Add(head)) continue;
                    queue.Enqueue(head);
                }
            }
            nexuses.Dispose();
        }
        return reached.Count;
    }

    private static double ShortestTreeCost(
        int vertexCount,
        GeneralArc[] arcs,
        int[] seeds,
        int target,
        bool bottleneck)
    {
        var byTail = new List<int>[vertexCount];
        for (int i = 0; i < vertexCount; i++) byTail[i] = [];
        for (int edge = 0; edge < arcs.Length; edge++)
            foreach (int tail in arcs[edge].Tail.Distinct()) byTail[tail].Add(edge);

        int[] remaining = arcs.Select(static edge => edge.Tail.Distinct().Count()).ToArray();
        double[] aggregate = bottleneck ? new double[arcs.Length] : new double[arcs.Length];
        double[] distance = Enumerable.Repeat(double.PositiveInfinity, vertexCount).ToArray();
        var finalized = new bool[vertexCount];
        var queue = new PriorityQueue<int, double>();
        foreach (int seed in seeds)
        {
            distance[seed] = 0;
            queue.Enqueue(seed, 0);
        }

        while (queue.TryDequeue(out int vertex, out double priority))
        {
            if (finalized[vertex] || priority != distance[vertex]) continue;
            finalized[vertex] = true;
            if (vertex == target) return priority;

            foreach (int edge in byTail[vertex])
            {
                aggregate[edge] = bottleneck
                    ? Math.Max(aggregate[edge], priority)
                    : aggregate[edge] + priority;
                if (--remaining[edge] != 0) continue;
                double candidate = bottleneck
                    ? Math.Max(aggregate[edge], arcs[edge].Cost)
                    : aggregate[edge] + arcs[edge].Cost;
                int head = arcs[edge].Head;
                if (candidate >= distance[head]) continue;
                distance[head] = candidate;
                queue.Enqueue(head, candidate);
            }
        }
        return double.PositiveInfinity;
    }

    private static double ExhaustiveUniqueArcCost(
        int vertexCount,
        GeneralArc[] arcs,
        int[] seeds,
        int target)
    {
        if (arcs.Length > 62) throw new ArgumentOutOfRangeException(nameof(arcs));
        double best = double.PositiveInfinity;
        ulong limit = 1UL << arcs.Length;
        for (ulong mask = 0; mask < limit; mask++)
        {
            double cost = 0;
            var selected = new List<GeneralArc>();
            for (int edge = 0; edge < arcs.Length; edge++)
            {
                if ((mask & (1UL << edge)) == 0) continue;
                cost += arcs[edge].Cost;
                selected.Add(arcs[edge]);
            }
            if (cost >= best) continue;
            if (FixedPointReachability(vertexCount, selected.ToArray(), seeds)[target])
                best = cost;
        }
        return best;
    }

    private readonly record struct GeneralArc(int[] Tail, int Head, double Cost);

    private sealed class TripleTailChain
    {
        private readonly int _edgeCount;
        private readonly int[] _offsets;
        private readonly int[] _incidences;

        private TripleTailChain(int edgeCount, int[] offsets, int[] incidences)
        {
            _edgeCount = edgeCount;
            _offsets = offsets;
            _incidences = incidences;
        }

        public static TripleTailChain Create(int edgeCount)
        {
            int vertexCount = edgeCount + 3;
            var degree = new int[vertexCount];
            for (int edge = 0; edge < edgeCount; edge++)
            {
                degree[0]++;
                degree[1]++;
                degree[edge + 2]++;
            }

            var offsets = new int[vertexCount + 1];
            for (int vertex = 0; vertex < vertexCount; vertex++)
                offsets[vertex + 1] = offsets[vertex] + degree[vertex];
            var cursor = offsets[..^1].ToArray();
            var incidences = new int[edgeCount * 3];
            for (int edge = 0; edge < edgeCount; edge++)
            {
                incidences[cursor[0]++] = edge;
                incidences[cursor[1]++] = edge;
                incidences[cursor[edge + 2]++] = edge;
            }
            return new TripleTailChain(edgeCount, offsets, incidences);
        }

        public long ReachableCount()
        {
            int vertexCount = _edgeCount + 3;
            var reached = new bool[vertexCount];
            var remaining = new byte[_edgeCount];
            Array.Fill(remaining, (byte)3);
            var queue = new int[vertexCount];
            int read = 0;
            int write = 3;
            reached[0] = reached[1] = reached[2] = true;
            queue[0] = 0;
            queue[1] = 1;
            queue[2] = 2;

            while (read < write)
            {
                int vertex = queue[read++];
                for (int i = _offsets[vertex]; i < _offsets[vertex + 1]; i++)
                {
                    int edge = _incidences[i];
                    if (--remaining[edge] != 0) continue;
                    int head = edge + 3;
                    if (reached[head]) continue;
                    reached[head] = true;
                    queue[write++] = head;
                }
            }
            return write;
        }
    }
}
