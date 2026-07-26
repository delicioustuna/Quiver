using System.Numerics;
using System.Runtime.CompilerServices;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks.Experimental.MathUseCases;

internal static class OptimizationAndProvenanceExperiments
{
    public static void Run()
    {
        Console.WriteLine("section=optimization-and-provenance");
        VerifyHittingSetAgainstExhaustiveOracle();
        MeasureHittingSetAtAnswerScale();
        MeasureSemiringHookOnCurrentTraversal();
        VerifyTropicalSpecialization();
        Console.WriteLine(
            "optimization.entail objective=absent depthLimit=20 limitReturn=NotRedundant integration=oracle-only");
    }

    private static void VerifyHittingSetAgainstExhaustiveOracle()
    {
        var random = new Random(0x48495453);
        long visited = 0;
        for (int trial = 0; trial < 60; trial++)
        {
            int candidateCount = 6 + random.Next(10);
            int factCount = 5 + random.Next(12);
            int[][] facts = new int[factCount][];
            for (int fact = 0; fact < factCount; fact++)
            {
                int width = 1 + random.Next(Math.Min(5, candidateCount));
                facts[fact] = Enumerable.Range(0, candidateCount)
                    .OrderBy(_ => random.Next())
                    .Take(width)
                    .Order()
                    .ToArray();
            }

            HittingSetResult expected = ExhaustiveHittingSet(facts, candidateCount);
            HittingSetResult actual = ExactHittingSet(facts, candidateCount);
            SpikeCheck.Equal(expected.Selected.Length, actual.Selected.Length,
                "branch-and-bound returned a non-minimum hitting set");
            SpikeCheck.True(CoversAll(facts, actual.Selected),
                "branch-and-bound returned a set that does not cover every fact");
            visited += actual.VisitedNodes;
        }

        int[][] greedyTrap =
        [
            [0, 1],
            [0, 1],
            [0, 2],
            [0, 2],
            [1, 3],
            [2, 4],
        ];
        int[] greedy = GreedyHittingSet(greedyTrap, 5);
        HittingSetResult exact = ExactHittingSet(greedyTrap, 5);
        SpikeCheck.Equal(3, greedy.Length, "greedy trap did not exercise approximation loss");
        SpikeCheck.Equal(2, exact.Selected.Length, "exact solver missed the two-source optimum");
        Console.WriteLine(
            $"optimization.oracle trials=60 visitedNodes={visited} greedyTrap={greedy.Length}->{exact.Selected.Length} result=PASS");
    }

    private static void MeasureHittingSetAtAnswerScale()
    {
        const int factCount = 500;
        const int candidateCount = 200;
        const int plantedOptimum = 8;
        var random = new Random(0x52414731);
        var facts = new int[factCount][];
        for (int fact = 0; fact < factCount; fact++)
        {
            int core = fact % plantedOptimum;
            int localDistractor = plantedOptimum + (fact % (candidateCount - plantedOptimum));
            int secondDistractor = plantedOptimum + random.Next(candidateCount - plantedOptimum);
            facts[fact] = new[] { core, localDistractor, secondDistractor }.Distinct().Order().ToArray();
        }

        HittingSetResult last = default;
        const int exactRepetitions = 7;
        AlternatingSamples samples = SpikeMeasurement.Alternate(
            () =>
            {
                for (int i = 0; i < exactRepetitions; i++) last = ExactHittingSet(facts, candidateCount);
                return last.Selected.Length;
            },
            () => SpikeMeasurement.Repeat(() => GreedyHittingSet(facts, candidateCount).Length, exactRepetitions),
            warmup: 1,
            samples: 7);

        SpikeCheck.Equal(plantedOptimum, last.Selected.Length,
            "answer-scale solver did not recover the planted optimum");
        double exactMilliseconds = samples.First.MedianMilliseconds / exactRepetitions;
        double greedyMilliseconds = samples.Second.MedianMilliseconds / exactRepetitions;
        string gate = exactMilliseconds < 100 ? "PASS" : "ASPIRATIONAL_MISS";
        Console.WriteLine(
            $"optimization.answer-scale shape=dominated facts={factCount} candidates={candidateCount} exact={last.Selected.Length} " +
            $"greedy={samples.Second.Checksum} exactMs={exactMilliseconds:F3} " +
            $"iqrPct={samples.First.IqrPercent:F2} " +
            $"greedyMs={greedyMilliseconds:F3} allocBytes={samples.First.AllocatedBytes / exactRepetitions} " +
            $"visitedNodes={last.VisitedNodes} gate={gate}");

        int[][] symmetric = CreateSymmetricAnswerScale();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        HittingSetResult bounded = ExactHittingSet(symmetric, candidateCount, nodeBudget: 20_000);
        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        int[] symmetricGreedy = GreedyHittingSet(symmetric, candidateCount);
        SpikeCheck.Equal(160, symmetricGreedy.Length,
            "symmetric answer-scale instance did not retain its known optimum upper bound");
        SpikeCheck.True(CoversAll(symmetric, bounded.Selected),
            "bounded solver lost feasibility on the symmetric instance");
        Console.WriteLine(
            $"optimization.answer-scale shape=symmetric facts={symmetric.Length} candidates={candidateCount} " +
            $"best={bounded.Selected.Length} knownOptimum=160 exact={bounded.Exact} elapsedMs={elapsed:F3} " +
            $"visitedNodes={bounded.VisitedNodes} nodeBudget=20000 decision={(bounded.Exact ? "EXACT" : "FALLBACK_REQUIRED")}");
    }

    private static void MeasureSemiringHookOnCurrentTraversal()
    {
        const int degree = 20_000;
        string directory = BenchTempDir.Create("math_semiring_hook");
        string path = Path.Combine(directory, "graph.quiver");
        try
        {
            using var db = QuiverDatabase.Open(path);
            VertexId hub;
            using (var write = db.BeginWriteTransaction())
            {
                hub = write.CreateVertex("Hub");
                for (int i = 0; i < degree; i++)
                    write.CreateEdge(hub, write.CreateVertex("Leaf"), "Link");
                write.Commit();
            }

            using var read = db.BeginReadTransaction();
            const int scanRepetitions = 8;
            AlternatingSamples samples = SpikeMeasurement.Alternate(
                () => SpikeMeasurement.Repeat(() => ScanWithoutAnnotation(read, hub), scanRepetitions),
                () => SpikeMeasurement.Repeat(
                    () => ScanWithAnnotation<Unit, UnitSemiring>(read, hub), scanRepetitions),
                warmup: 4,
                samples: 17);
            SpikeCheck.Equal(samples.First.Checksum, samples.Second.Checksum,
                "annotation hook changed traversal output");

            double overhead = (samples.SecondOverFirst - 1.0) * 100.0;
            string gate = overhead <= 5.0 ? "PASS" : "CORRECTIVE";
            Console.WriteLine(
                $"provenance.no-annotation degree={degree} baselineMs={samples.First.MedianMilliseconds / scanRepetitions:F3} " +
                $"hookMs={samples.Second.MedianMilliseconds / scanRepetitions:F3} overheadPct={overhead:F2} " +
                $"baselineIqrPct={samples.First.IqrPercent:F2} hookIqrPct={samples.Second.IqrPercent:F2} " +
                $"baselineAlloc={samples.First.AllocatedBytes / scanRepetitions} " +
                $"hookAlloc={samples.Second.AllocatedBytes / scanRepetitions} gate={gate}");
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    private static void VerifyTropicalSpecialization()
    {
        WeightedArc[][] graph = CreateWeightedGraph(400, 2_800, seed: 0x54524f50);
        double[] expected = BaselineDijkstra(graph, 0);
        double[] actual = SemiringDijkstra<TropicalSemiring>(graph, 0);
        for (int i = 0; i < expected.Length; i++)
        {
            if (double.IsPositiveInfinity(expected[i]))
            {
                SpikeCheck.True(double.IsPositiveInfinity(actual[i]), "tropical path made an unreachable vertex reachable");
                continue;
            }
            SpikeCheck.True(Math.Abs(expected[i] - actual[i]) <= 1e-10,
                "tropical specialization disagreed with Dijkstra");
        }
        Console.WriteLine("provenance.tropical vertices=400 edges=2800 dijkstraAgreement=PASS");
    }

    private static int[][] CreateSymmetricAnswerScale()
    {
        var facts = new List<int[]>(500);
        const int blockSize = 5;
        const int blockCount = 40;
        for (int block = 0; block < blockCount; block++)
        {
            int start = block * blockSize;
            for (int a = 0; a < blockSize; a++)
                for (int b = a + 1; b < blockSize; b++)
                    facts.Add([start + a, start + b]);
        }
        for (int i = 0; i < 100; i++) facts.Add((int[])facts[i].Clone());
        return facts.ToArray();
    }

    private static HittingSetResult ExactHittingSet(
        int[][] facts,
        int candidateCount,
        long nodeBudget = long.MaxValue)
    {
        if (facts.Any(static fact => fact.Length == 0))
            return new HittingSetResult([], 0, false);

        int wordCount = (facts.Length + 63) / 64;
        var coverage = new ulong[candidateCount][];
        for (int candidate = 0; candidate < candidateCount; candidate++)
            coverage[candidate] = new ulong[wordCount];
        for (int fact = 0; fact < facts.Length; fact++)
            foreach (int candidate in facts[fact])
                coverage[candidate][fact >> 6] |= 1UL << (fact & 63);

        bool[] active = RemoveDominatedCandidates(coverage);
        int[] greedy = GreedyHittingSet(facts, candidateCount, active);
        var best = greedy.ToList();
        long visited = 0;
        bool exhausted = false;
        var selected = new List<int>();
        var selectedFlags = new bool[candidateCount];
        var empty = new ulong[wordCount];

        Search(empty);
        return new HittingSetResult(best.Order().ToArray(), visited, !exhausted);

        void Search(ulong[] covered)
        {
            if (visited >= nodeBudget)
            {
                exhausted = true;
                return;
            }
            visited++;
            int uncoveredCount = facts.Length - CountBits(covered, facts.Length);
            if (uncoveredCount == 0)
            {
                if (selected.Count < best.Count) best = [.. selected];
                return;
            }
            if (selected.Count >= best.Count - 1) return;

            int maximumAdditional = 0;
            for (int candidate = 0; candidate < candidateCount; candidate++)
            {
                if (!active[candidate] || selectedFlags[candidate]) continue;
                maximumAdditional = Math.Max(maximumAdditional, CountNewBits(coverage[candidate], covered));
            }
            if (maximumAdditional == 0) return;
            int lowerBound = (uncoveredCount + maximumAdditional - 1) / maximumAdditional;
            if (selected.Count + lowerBound >= best.Count) return;

            int branchFact = ChooseBranchFact(facts, covered, active, selectedFlags);
            int[] options = facts[branchFact]
                .Where(candidate => active[candidate] && !selectedFlags[candidate])
                .OrderByDescending(candidate => CountNewBits(coverage[candidate], covered))
                .ThenBy(candidate => candidate)
                .ToArray();
            foreach (int candidate in options)
            {
                if (exhausted) return;
                var next = (ulong[])covered.Clone();
                OrInto(next, coverage[candidate]);
                selected.Add(candidate);
                selectedFlags[candidate] = true;
                Search(next);
                selectedFlags[candidate] = false;
                selected.RemoveAt(selected.Count - 1);
            }
        }
    }

    private static bool[] RemoveDominatedCandidates(ulong[][] coverage)
    {
        var active = Enumerable.Repeat(true, coverage.Length).ToArray();
        for (int candidate = 0; candidate < coverage.Length; candidate++)
        {
            if (IsEmpty(coverage[candidate]))
            {
                active[candidate] = false;
                continue;
            }
            for (int other = 0; other < coverage.Length; other++)
            {
                if (candidate == other) continue;
                if (!IsSubset(coverage[candidate], coverage[other])) continue;
                if (IsSubset(coverage[other], coverage[candidate]) && candidate < other) continue;
                active[candidate] = false;
                break;
            }
        }
        return active;
    }

    private static int[] GreedyHittingSet(int[][] facts, int candidateCount, bool[]? active = null)
    {
        active ??= Enumerable.Repeat(true, candidateCount).ToArray();
        var uncovered = Enumerable.Repeat(true, facts.Length).ToArray();
        var selected = new List<int>();
        int remaining = facts.Length;
        while (remaining > 0)
        {
            int bestCandidate = -1;
            int bestCover = 0;
            for (int candidate = 0; candidate < candidateCount; candidate++)
            {
                if (!active[candidate] || selected.Contains(candidate)) continue;
                int cover = 0;
                for (int fact = 0; fact < facts.Length; fact++)
                    if (uncovered[fact] && Array.BinarySearch(facts[fact], candidate) >= 0) cover++;
                if (cover <= bestCover) continue;
                bestCover = cover;
                bestCandidate = candidate;
            }
            if (bestCandidate < 0) return [];
            selected.Add(bestCandidate);
            for (int fact = 0; fact < facts.Length; fact++)
            {
                if (!uncovered[fact] || Array.BinarySearch(facts[fact], bestCandidate) < 0) continue;
                uncovered[fact] = false;
                remaining--;
            }
        }
        return selected.ToArray();
    }

    private static HittingSetResult ExhaustiveHittingSet(int[][] facts, int candidateCount)
    {
        if (candidateCount > 62) throw new ArgumentOutOfRangeException(nameof(candidateCount));
        int bestSize = int.MaxValue;
        ulong bestMask = 0;
        ulong limit = 1UL << candidateCount;
        for (ulong mask = 0; mask < limit; mask++)
        {
            int size = BitOperations.PopCount(mask);
            if (size >= bestSize) continue;
            bool covers = true;
            foreach (int[] fact in facts)
            {
                bool hit = false;
                foreach (int candidate in fact)
                {
                    if ((mask & (1UL << candidate)) == 0) continue;
                    hit = true;
                    break;
                }
                if (hit) continue;
                covers = false;
                break;
            }
            if (!covers) continue;
            bestSize = size;
            bestMask = mask;
        }

        int[] selected = Enumerable.Range(0, candidateCount)
            .Where(candidate => (bestMask & (1UL << candidate)) != 0)
            .ToArray();
        return new HittingSetResult(selected, (long)limit, bestSize != int.MaxValue);
    }

    private static bool CoversAll(int[][] facts, int[] selected)
    {
        var set = selected.ToHashSet();
        return facts.All(fact => fact.Any(set.Contains));
    }

    private static int ChooseBranchFact(int[][] facts, ulong[] covered, bool[] active, bool[] selected)
    {
        int bestFact = -1;
        int bestWidth = int.MaxValue;
        for (int fact = 0; fact < facts.Length; fact++)
        {
            if ((covered[fact >> 6] & (1UL << (fact & 63))) != 0) continue;
            int width = facts[fact].Count(candidate => active[candidate] && !selected[candidate]);
            if (width >= bestWidth) continue;
            bestWidth = width;
            bestFact = fact;
        }
        return bestFact;
    }

    private static int CountBits(ulong[] words, int bitCount)
    {
        int count = 0;
        foreach (ulong word in words) count += BitOperations.PopCount(word);
        return Math.Min(count, bitCount);
    }

    private static int CountNewBits(ulong[] candidate, ulong[] covered)
    {
        int count = 0;
        for (int i = 0; i < candidate.Length; i++)
            count += BitOperations.PopCount(candidate[i] & ~covered[i]);
        return count;
    }

    private static void OrInto(ulong[] target, ulong[] source)
    {
        for (int i = 0; i < target.Length; i++) target[i] |= source[i];
    }

    private static bool IsEmpty(ulong[] words) => words.All(static word => word == 0);

    private static bool IsSubset(ulong[] subset, ulong[] superset)
    {
        for (int i = 0; i < subset.Length; i++)
            if ((subset[i] & ~superset[i]) != 0) return false;
        return true;
    }

    private static long ScanWithoutAnnotation(IReadTransaction transaction, VertexId hub)
    {
        long checksum = 0;
        int count = 0;
        var edges = transaction.EnumerateEdges(hub, Direction.Outgoing, "Link");
        while (edges.MoveNext())
        {
            checksum ^= edges.Current.Target.Sequence;
            count++;
        }
        edges.Dispose();
        return checksum ^ count;
    }

    private static long ScanWithAnnotation<T, TSemiring>(IReadTransaction transaction, VertexId hub)
        where TSemiring : struct, IStaticSemiring<T>
    {
        T annotation = TSemiring.One;
        long checksum = 0;
        int count = 0;
        var edges = transaction.EnumerateEdges(hub, Direction.Outgoing, "Link");
        while (edges.MoveNext())
        {
            annotation = TSemiring.Add(annotation, TSemiring.Multiply(TSemiring.One, TSemiring.One));
            checksum ^= edges.Current.Target.Sequence;
            count++;
        }
        edges.Dispose();
        Consume(annotation);
        return checksum ^ count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Consume<T>(T value)
    {
        if (typeof(T) == typeof(NeverMaterialized))
            throw new InvalidOperationException(value?.ToString());
    }

    private static WeightedArc[][] CreateWeightedGraph(int vertexCount, int edgeCount, int seed)
    {
        var random = new Random(seed);
        var lists = Enumerable.Range(0, vertexCount).Select(_ => new List<WeightedArc>()).ToArray();
        for (int vertex = 1; vertex < vertexCount; vertex++)
            lists[vertex - 1].Add(new WeightedArc(vertex, 0.1 + random.NextDouble()));
        for (int edge = vertexCount - 1; edge < edgeCount; edge++)
        {
            int source = random.Next(vertexCount);
            int target = random.Next(vertexCount);
            if (source == target) target = (target + 1) % vertexCount;
            lists[source].Add(new WeightedArc(target, 0.1 + random.NextDouble() * 9.9));
        }
        return lists.Select(static list => list.ToArray()).ToArray();
    }

    private static double[] BaselineDijkstra(WeightedArc[][] graph, int source)
    {
        double[] distance = Enumerable.Repeat(double.PositiveInfinity, graph.Length).ToArray();
        distance[source] = 0;
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(source, 0);
        while (queue.TryDequeue(out int vertex, out double priority))
        {
            if (priority != distance[vertex]) continue;
            foreach (WeightedArc edge in graph[vertex])
            {
                double candidate = priority + edge.Weight;
                if (candidate >= distance[edge.Target]) continue;
                distance[edge.Target] = candidate;
                queue.Enqueue(edge.Target, candidate);
            }
        }
        return distance;
    }

    private static double[] SemiringDijkstra<TSemiring>(WeightedArc[][] graph, int source)
        where TSemiring : struct, IDoublePathSemiring
    {
        double[] distance = Enumerable.Repeat(TSemiring.Zero, graph.Length).ToArray();
        distance[source] = TSemiring.One;
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(source, TSemiring.One);
        while (queue.TryDequeue(out int vertex, out double priority))
        {
            if (priority != distance[vertex]) continue;
            foreach (WeightedArc edge in graph[vertex])
            {
                double candidate = TSemiring.Multiply(priority, edge.Weight);
                double merged = TSemiring.Add(distance[edge.Target], candidate);
                if (merged == distance[edge.Target]) continue;
                distance[edge.Target] = merged;
                queue.Enqueue(edge.Target, merged);
            }
        }
        return distance;
    }

    private readonly record struct HittingSetResult(int[] Selected, long VisitedNodes, bool Exact);
    private readonly record struct WeightedArc(int Target, double Weight);
    private readonly record struct Unit;
    private readonly record struct NeverMaterialized;

    private interface IStaticSemiring<T>
    {
        static abstract T One { get; }
        static abstract T Add(T left, T right);
        static abstract T Multiply(T left, T right);
    }

    private readonly struct UnitSemiring : IStaticSemiring<Unit>
    {
        public static Unit One => default;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Unit Add(Unit left, Unit right) => default;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Unit Multiply(Unit left, Unit right) => default;
    }

    private interface IDoublePathSemiring
    {
        static abstract double Zero { get; }
        static abstract double One { get; }
        static abstract double Add(double left, double right);
        static abstract double Multiply(double left, double right);
    }

    private readonly struct TropicalSemiring : IDoublePathSemiring
    {
        public static double Zero => double.PositiveInfinity;
        public static double One => 0;
        public static double Add(double left, double right) => Math.Min(left, right);
        public static double Multiply(double left, double right) => left + right;
    }
}
