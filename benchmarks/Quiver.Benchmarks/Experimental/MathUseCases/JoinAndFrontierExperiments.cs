using System.Diagnostics;

namespace Quiver.Benchmarks.Experimental.MathUseCases;

internal static class JoinAndFrontierExperiments
{
    public static void Run()
    {
        Console.WriteLine("section=joins-and-frontier");
        VerifyTriangleJoinOracle();
        MeasureTriangleCrossover();
        VerifyStructuralRouting();
        VerifyFrontierKernels();
    }

    private static void VerifyTriangleJoinOracle()
    {
        TriangleData data = TriangleData.Create(10);
        long[] binary = BinaryTriangleTuples(data).Order().ToArray();
        long[] leapfrog = IntersectionTriangleTuples(data).Order().ToArray();
        SpikeCheck.SequenceEqual(binary, leapfrog, "sorted intersection changed triangle results");
        SpikeCheck.Equal(100, binary.Length, "typed triangle dataset produced an unexpected output size");
        Console.WriteLine("join.oracle domain=typed-triangle tuples=100 result=PASS");
    }

    private static void MeasureTriangleCrossover()
    {
        int crossover = -1;
        foreach (int domain in new[] { 16, 32, 64, 128 })
        {
            long buildStart = Stopwatch.GetTimestamp();
            TriangleData data = TriangleData.Create(domain);
            double buildMilliseconds = Stopwatch.GetElapsedTime(buildStart).TotalMilliseconds;
            long expected = (long)domain * domain;
            AlternatingSamples samples = SpikeMeasurement.Alternate(
                () => BinaryMaterializingCount(data),
                () => IntersectionCount(data),
                warmup: 1,
                samples: 7);
            SpikeCheck.Equal(expected, samples.First.Checksum, "binary join produced an unexpected triangle count");
            SpikeCheck.Equal(expected, samples.Second.Checksum, "intersection join produced an unexpected triangle count");
            if (crossover < 0 && samples.Second.MedianMilliseconds < samples.First.MedianMilliseconds)
                crossover = domain;
            double memoryRatio = samples.Second.AllocatedBytes == 0
                ? double.PositiveInfinity
                : samples.First.AllocatedBytes / (double)samples.Second.AllocatedBytes;
            Console.WriteLine(
                $"join.scale domain={domain} relationRows={2L * domain * domain + domain} output={expected} " +
                $"buildMs={buildMilliseconds:F3} binaryMs={samples.First.MedianMilliseconds:F3} " +
                $"binaryIqrPct={samples.First.IqrPercent:F2} intersectionMs={samples.Second.MedianMilliseconds:F3} " +
                $"intersectionIqrPct={samples.Second.IqrPercent:F2} speedup={1.0 / samples.SecondOverFirst:F2}x " +
                $"binaryAlloc={samples.First.AllocatedBytes} intersectionAlloc={samples.Second.AllocatedBytes} " +
                $"allocationRatio={(double.IsPositiveInfinity(memoryRatio) ? "inf" : memoryRatio.ToString("F1"))}x");
        }
        string gate = crossover > 0 ? "PASS" : "HARD_MISS";
        Console.WriteLine($"join.crossover minimumDomain={crossover} gate={gate}");
    }

    private static void VerifyStructuralRouting()
    {
        int[][] path = [[0, 1], [1, 2], [2, 3]];
        int[][] star = [[0, 1], [0, 2], [0, 3], [0, 4]];
        int[][] triangle = [[0, 1], [1, 2], [0, 2]];
        SpikeCheck.True(IsAcyclicByGyo(path), "path query was classified as cyclic");
        SpikeCheck.True(IsAcyclicByGyo(star), "star query was classified as cyclic");
        SpikeCheck.True(!IsAcyclicByGyo(triangle), "triangle query was classified as acyclic");
        Console.WriteLine("join.routing path=Yannakakis star=Yannakakis triangle=intersection result=PASS");
    }

    private static void VerifyFrontierKernels()
    {
        double pushForwardError = PushForwardError();
        bool pullbackComposes = PullbackCompositionHolds();
        double laplacianEnergy = HigherOrderLaplacianEnergy([1.0, -2.0, 0.5]);
        double dmdError = DynamicModeDecompositionError();
        int poorWidth = InducedWidth(StarAdjacency(9), Enumerable.Range(0, 9).ToArray());
        int greedyWidth = InducedWidth(StarAdjacency(9), GreedyMinimumDegreeOrder(StarAdjacency(9)));

        SpikeCheck.True(pushForwardError < 1e-12, "linear representation push-forward was incorrect");
        SpikeCheck.True(pullbackComposes, "projection pullback did not compose");
        SpikeCheck.True(laplacianEnergy >= -1e-12, "higher-order Laplacian was not positive semidefinite");
        SpikeCheck.True(dmdError < 1e-10, "DMD failed to recover a linear latent transition");
        SpikeCheck.True(greedyWidth < poorWidth, "decomposition heuristic did not reduce contraction width");
        Console.WriteLine(
            $"frontier.kernels pushForwardError={pushForwardError:E2} pullback={pullbackComposes} " +
            $"hodgeEnergy={laplacianEnergy:F4} dmdError={dmdError:E2} " +
            $"contractionWidth={poorWidth}->{greedyWidth} feasibility=PASS demandAndApi=UNVERIFIED");
    }

    private static long BinaryMaterializingCount(TriangleData data)
    {
        var intermediate = new List<(int A, int C)>(data.Domain * data.Domain * data.Domain);
        for (int a = 0; a < data.Domain; a++)
            foreach (int b in data.RByA[a])
                foreach (int c in data.SByB[b])
                    intermediate.Add((a, c));

        long count = 0;
        foreach ((int a, int c) in intermediate)
            if (Array.BinarySearch(data.TByC[c], a) >= 0) count++;
        return count;
    }

    private static long IntersectionCount(TriangleData data)
    {
        long count = 0;
        for (int a = 0; a < data.Domain; a++)
        {
            int[] permittedC = data.TByA[a];
            foreach (int b in data.RByA[a])
                count += IntersectCount(data.SByB[b], permittedC);
        }
        return count;
    }

    private static IEnumerable<long> BinaryTriangleTuples(TriangleData data)
    {
        for (int a = 0; a < data.Domain; a++)
            foreach (int b in data.RByA[a])
                foreach (int c in data.SByB[b])
                    if (Array.BinarySearch(data.TByC[c], a) >= 0) yield return Encode(a, b, c);
    }

    private static IEnumerable<long> IntersectionTriangleTuples(TriangleData data)
    {
        for (int a = 0; a < data.Domain; a++)
        {
            int[] permittedC = data.TByA[a];
            foreach (int b in data.RByA[a])
            {
                foreach (int c in Intersect(data.SByB[b], permittedC))
                    yield return Encode(a, b, c);
            }
        }
    }

    private static int IntersectCount(int[] left, int[] right)
    {
        int i = 0;
        int j = 0;
        int count = 0;
        while (i < left.Length && j < right.Length)
        {
            if (left[i] < right[j]) i++;
            else if (right[j] < left[i]) j++;
            else
            {
                count++;
                i++;
                j++;
            }
        }
        return count;
    }

    private static IEnumerable<int> Intersect(int[] left, int[] right)
    {
        int i = 0;
        int j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (left[i] < right[j]) i++;
            else if (right[j] < left[i]) j++;
            else
            {
                yield return left[i];
                i++;
                j++;
            }
        }
    }

    private static long Encode(int a, int b, int c) => ((long)a << 42) | ((long)b << 21) | (uint)c;

    private static bool IsAcyclicByGyo(int[][] hyperedges)
    {
        var edges = hyperedges.Select(edge => edge.ToHashSet()).ToList();
        bool changed;
        do
        {
            changed = false;
            var counts = new Dictionary<int, int>();
            foreach (HashSet<int> edge in edges)
                foreach (int vertex in edge) counts[vertex] = counts.GetValueOrDefault(vertex) + 1;
            foreach (HashSet<int> edge in edges)
                if (edge.RemoveWhere(vertex => counts[vertex] <= 1) > 0) changed = true;

            for (int i = edges.Count - 1; i >= 0; i--)
            {
                if (edges[i].Count == 0 || edges.Where((_, j) => j != i).Any(other => edges[i].IsSubsetOf(other)))
                {
                    edges.RemoveAt(i);
                    changed = true;
                }
            }
        }
        while (changed);
        return edges.Count == 0;
    }

    private static double PushForwardError()
    {
        double[] source = [2, -1];
        double[,] first = { { 1, 2 }, { 0, -1 } };
        double[,] second = { { 0.5, 0 }, { 1, 1 } };
        double[] actual = Add(MatrixVector(first, source), MatrixVector(second, source));
        double[] expected = [0.0, 0.0];
        expected[0] = (2 - 2) + 1;
        expected[1] = 1 + 1;
        return Math.Sqrt(actual.Zip(expected, static (a, b) => (a - b) * (a - b)).Sum());
    }

    private static bool PullbackCompositionHolds()
    {
        var records = new[]
        {
            new Dictionary<string, string> { ["employee"] = "Ada", ["department"] = "Research" },
            new Dictionary<string, string> { ["employee"] = "Lin", ["department"] = "Systems" },
        };
        var first = new Dictionary<string, string> { ["person"] = "employee", ["group"] = "department" };
        var second = new Dictionary<string, string> { ["name"] = "person" };
        Dictionary<string, string> composed = second.ToDictionary(
            pair => pair.Key,
            pair => first[pair.Value]);
        string[] stepwise = records.Select(row => row[first[second["name"]]]).ToArray();
        string[] direct = records.Select(row => row[composed["name"]]).ToArray();
        return stepwise.SequenceEqual(direct);
    }

    private static double HigherOrderLaplacianEnergy(double[] edgeSignal)
    {
        double[,] vertexBoundary =
        {
            { -1, 0, -1 },
            { 1, -1, 0 },
            { 0, 1, 1 },
        };
        double[] faceBoundary = [1, 1, -1];
        double[] divergence = new double[3];
        for (int vertex = 0; vertex < 3; vertex++)
            for (int edge = 0; edge < 3; edge++)
                divergence[vertex] += vertexBoundary[vertex, edge] * edgeSignal[edge];
        double curl = Dot(faceBoundary, edgeSignal);
        return Dot(divergence, divergence) + curl * curl;
    }

    private static double DynamicModeDecompositionError()
    {
        double angle = 0.17;
        double[,] expected =
        {
            { Math.Cos(angle), -Math.Sin(angle) },
            { Math.Sin(angle), Math.Cos(angle) },
        };
        var current = new double[] { 1, 0.25 };
        var x = new List<double[]>();
        var y = new List<double[]>();
        for (int step = 0; step < 80; step++)
        {
            double[] next = MatrixVector(expected, current);
            x.Add(current);
            y.Add(next);
            current = next;
        }

        double xx00 = 0, xx01 = 0, xx11 = 0;
        double yx00 = 0, yx01 = 0, yx10 = 0, yx11 = 0;
        for (int i = 0; i < x.Count; i++)
        {
            xx00 += x[i][0] * x[i][0];
            xx01 += x[i][0] * x[i][1];
            xx11 += x[i][1] * x[i][1];
            yx00 += y[i][0] * x[i][0];
            yx01 += y[i][0] * x[i][1];
            yx10 += y[i][1] * x[i][0];
            yx11 += y[i][1] * x[i][1];
        }
        double determinant = xx00 * xx11 - xx01 * xx01;
        double inv00 = xx11 / determinant;
        double inv01 = -xx01 / determinant;
        double inv11 = xx00 / determinant;
        double[,] actual =
        {
            { yx00 * inv00 + yx01 * inv01, yx00 * inv01 + yx01 * inv11 },
            { yx10 * inv00 + yx11 * inv01, yx10 * inv01 + yx11 * inv11 },
        };
        double error = 0;
        for (int row = 0; row < 2; row++)
            for (int column = 0; column < 2; column++)
                error += Math.Pow(expected[row, column] - actual[row, column], 2);
        return Math.Sqrt(error);
    }

    private static HashSet<int>[] StarAdjacency(int count)
    {
        var graph = Enumerable.Range(0, count).Select(_ => new HashSet<int>()).ToArray();
        for (int leaf = 1; leaf < count; leaf++)
        {
            graph[0].Add(leaf);
            graph[leaf].Add(0);
        }
        return graph;
    }

    private static int[] GreedyMinimumDegreeOrder(HashSet<int>[] source)
    {
        HashSet<int>[] graph = source.Select(set => set.ToHashSet()).ToArray();
        var remaining = Enumerable.Range(0, graph.Length).ToHashSet();
        var order = new int[graph.Length];
        for (int position = 0; position < order.Length; position++)
        {
            int vertex = remaining.OrderBy(v => graph[v].Count(remaining.Contains)).ThenBy(v => v).First();
            order[position] = vertex;
            Eliminate(graph, remaining, vertex);
        }
        return order;
    }

    private static int InducedWidth(HashSet<int>[] source, int[] order)
    {
        HashSet<int>[] graph = source.Select(set => set.ToHashSet()).ToArray();
        var remaining = Enumerable.Range(0, graph.Length).ToHashSet();
        int width = 0;
        foreach (int vertex in order)
        {
            width = Math.Max(width, graph[vertex].Count(remaining.Contains));
            Eliminate(graph, remaining, vertex);
        }
        return width;
    }

    private static void Eliminate(HashSet<int>[] graph, HashSet<int> remaining, int vertex)
    {
        int[] neighbors = graph[vertex].Where(remaining.Contains).ToArray();
        for (int i = 0; i < neighbors.Length; i++)
            for (int j = i + 1; j < neighbors.Length; j++)
            {
                graph[neighbors[i]].Add(neighbors[j]);
                graph[neighbors[j]].Add(neighbors[i]);
            }
        remaining.Remove(vertex);
    }

    private static double[] MatrixVector(double[,] matrix, double[] vector)
    {
        var result = new double[matrix.GetLength(0)];
        for (int row = 0; row < result.Length; row++)
            for (int column = 0; column < vector.Length; column++)
                result[row] += matrix[row, column] * vector[column];
        return result;
    }

    private static double[] Add(double[] left, double[] right)
        => left.Zip(right, static (a, b) => a + b).ToArray();

    private static double Dot(double[] left, double[] right)
    {
        double sum = 0;
        for (int i = 0; i < left.Length; i++) sum += left[i] * right[i];
        return sum;
    }

    private sealed class TriangleData
    {
        private TriangleData(int domain, int[][] rByA, int[][] sByB, int[][] tByA, int[][] tByC)
        {
            Domain = domain;
            RByA = rByA;
            SByB = sByB;
            TByA = tByA;
            TByC = tByC;
        }

        public int Domain { get; }
        public int[][] RByA { get; }
        public int[][] SByB { get; }
        public int[][] TByA { get; }
        public int[][] TByC { get; }

        public static TriangleData Create(int domain)
        {
            int[] all = Enumerable.Range(0, domain).ToArray();
            int[][] rByA = Enumerable.Range(0, domain).Select(_ => (int[])all.Clone()).ToArray();
            int[][] sByB = Enumerable.Range(0, domain).Select(_ => (int[])all.Clone()).ToArray();
            int[][] tByA = Enumerable.Range(0, domain).Select(a => new[] { a }).ToArray();
            int[][] tByC = Enumerable.Range(0, domain).Select(c => new[] { c }).ToArray();
            return new TriangleData(domain, rByA, sByB, tByA, tByC);
        }
    }
}
