namespace Quiver.Benchmarks.Experimental.MathUseCases;

internal static class RankingExperiments
{
    public static void Run()
    {
        Console.WriteLine("section=ranking");
        VerifyExactGradientRecovery();
        MeasureSparseRanking();
        VerifyCurlSensitivity();
        Console.WriteLine("ranking.apply-dyadic contract=fixed-reference-top-k pairFlowAdapter=required");
    }

    private static void VerifyExactGradientRecovery()
    {
        double[] latent = [0, 2, -1, 4, 3];
        Comparison[] comparisons =
        [
            GradientComparison(0, 1, latent),
            GradientComparison(1, 2, latent),
            GradientComparison(2, 3, latent),
            GradientComparison(3, 4, latent),
            GradientComparison(0, 4, latent),
            GradientComparison(1, 3, latent),
        ];
        HodgeSolution solution = SolveHodge(latent.Length, comparisons, tolerance: 1e-12);
        double offset = latent[0] - solution.Scores[0];
        for (int i = 0; i < latent.Length; i++)
            SpikeCheck.True(Math.Abs(latent[i] - (solution.Scores[i] + offset)) < 1e-8,
                "matrix-free Laplacian failed to recover an exact gradient flow");
        Console.WriteLine(
            $"ranking.exact-gradient vertices={latent.Length} iterations={solution.Iterations} residual={solution.RelativeResidual:E2} result=PASS");
    }

    private static void MeasureSparseRanking()
    {
        const int vertexCount = 10_000;
        const int edgeCount = 100_000;
        (double[] latent, Comparison[] comparisons) = CreateComparisons(vertexCount, edgeCount, seed: 0x484f4447);

        HodgeSolution hodge = default;
        double[] btl = [];
        AlternatingSamples samples = SpikeMeasurement.Alternate(
            () =>
            {
                hodge = SolveHodge(vertexCount, comparisons, tolerance: 1e-8);
                return hodge.Iterations;
            },
            () =>
            {
                btl = SolveBradleyTerry(vertexCount, comparisons, 80);
                return BitConverter.DoubleToInt64Bits(btl[vertexCount / 2]);
            },
            warmup: 1,
            samples: 5);

        double hodgeTau = KendallTau(latent, hodge.Scores);
        double btlTau = KendallTau(latent, btl);
        SpikeCheck.True(hodge.RelativeResidual <= 1e-6,
            $"CG did not converge to the requested residual: {hodge.RelativeResidual:E2}");
        SpikeCheck.True(hodge.Iterations < vertexCount / 10,
            $"CG iterations were not small relative to N: {hodge.Iterations}/{vertexCount}");
        string qualityGate = hodgeTau + 1e-9 >= btlTau ? "PASS" : "HARD_MISS";
        string timeGate = samples.First.MedianMilliseconds < 500 ? "PASS" : "ASPIRATIONAL_MISS";
        Console.WriteLine(
            $"ranking.sparse vertices={vertexCount} edges={edgeCount} cgIterations={hodge.Iterations} " +
            $"cgMs={samples.First.MedianMilliseconds:F3} cgIqrPct={samples.First.IqrPercent:F2} " +
            $"btlMs={samples.Second.MedianMilliseconds:F3} btlIqrPct={samples.Second.IqrPercent:F2} " +
            $"cgAlloc={samples.First.AllocatedBytes} hodgeTau={hodgeTau:F4} btlTau={btlTau:F4} " +
            $"qualityGate={qualityGate} timeGate={timeGate}");
    }

    private static void VerifyCurlSensitivity()
    {
        double[] scores = [0.0, 1.0, 3.0, -1.0];
        var clean = new Dictionary<(int, int), double>
        {
            [(0, 1)] = scores[1] - scores[0],
            [(1, 2)] = scores[2] - scores[1],
            [(0, 2)] = scores[2] - scores[0],
            [(0, 3)] = scores[3] - scores[0],
            [(2, 3)] = scores[3] - scores[2],
        };
        double cleanCurl = TriangleCurlRms(clean, [(0, 1, 2), (0, 2, 3)]);
        clean[(0, 2)] += 2.5;
        double inconsistentCurl = TriangleCurlRms(clean, [(0, 1, 2), (0, 2, 3)]);
        SpikeCheck.True(cleanCurl < 1e-12, "gradient flow had non-zero curl");
        SpikeCheck.True(inconsistentCurl > 1.0, "injected cycle inconsistency did not raise curl");
        Console.WriteLine(
            $"ranking.curl clean={cleanCurl:E2} injected={inconsistentCurl:F4} result=PASS");
    }

    private static Comparison GradientComparison(int a, int b, double[] scores)
        => new(a, b, scores[b] - scores[a], 1, 1, 1.0);

    private static (double[] Latent, Comparison[] Comparisons) CreateComparisons(
        int vertexCount,
        int edgeCount,
        int seed)
    {
        var random = new Random(seed);
        var latent = new double[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            latent[i] = NextGaussian(random);

        var pairs = new HashSet<long>();
        var comparisons = new List<Comparison>(edgeCount);
        for (int i = 1; i < vertexCount; i++)
            Add(i - 1, i);
        while (comparisons.Count < edgeCount)
        {
            int a = random.Next(vertexCount);
            int b = random.Next(vertexCount);
            if (a == b) continue;
            if (a > b) (a, b) = (b, a);
            Add(a, b);
        }
        return (latent, comparisons.ToArray());

        void Add(int a, int b)
        {
            long key = ((long)a << 32) | (uint)b;
            if (!pairs.Add(key)) return;
            const int trials = 5;
            double probabilityB = 1.0 / (1.0 + Math.Exp(-(latent[b] - latent[a])));
            int winsB = 0;
            for (int trial = 0; trial < trials; trial++)
                if (random.NextDouble() < probabilityB) winsB++;
            int winsA = trials - winsB;
            double flow = (winsB - winsA) / (double)trials;
            comparisons.Add(new Comparison(a, b, flow, winsA, winsB, trials));
        }
    }

    private static HodgeSolution SolveHodge(
        int vertexCount,
        Comparison[] comparisons,
        double tolerance)
    {
        var right = new double[vertexCount];
        var diagonal = new double[vertexCount];
        foreach (Comparison comparison in comparisons)
        {
            double weightedFlow = comparison.Weight * comparison.Flow;
            right[comparison.A] -= weightedFlow;
            right[comparison.B] += weightedFlow;
            diagonal[comparison.A] += comparison.Weight;
            diagonal[comparison.B] += comparison.Weight;
        }

        right[0] = 0;
        diagonal[0] = 1;
        var x = new double[vertexCount];
        var residual = (double[])right.Clone();
        var z = new double[vertexCount];
        var direction = new double[vertexCount];
        var product = new double[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            z[i] = residual[i] / diagonal[i];
        Array.Copy(z, direction, vertexCount);
        double rz = Dot(residual, z);
        double initialNorm = Math.Sqrt(Dot(residual, residual));
        if (initialNorm == 0) return new HodgeSolution(x, 0, 0);

        int iteration = 0;
        double relativeResidual = 1;
        int maximumIterations = Math.Min(vertexCount, 2_000);
        for (; iteration < maximumIterations; iteration++)
        {
            ApplyLaplacian(comparisons, direction, product);
            double denominator = Dot(direction, product);
            if (Math.Abs(denominator) < 1e-30) break;
            double alpha = rz / denominator;
            Axpy(x, direction, alpha);
            Axpy(residual, product, -alpha);
            relativeResidual = Math.Sqrt(Dot(residual, residual)) / initialNorm;
            if (relativeResidual <= tolerance)
            {
                iteration++;
                break;
            }

            for (int i = 0; i < vertexCount; i++)
                z[i] = residual[i] / diagonal[i];
            double nextRz = Dot(residual, z);
            double beta = nextRz / rz;
            for (int i = 0; i < vertexCount; i++)
                direction[i] = z[i] + beta * direction[i];
            rz = nextRz;
        }
        return new HodgeSolution(x, iteration, relativeResidual);
    }

    private static void ApplyLaplacian(Comparison[] comparisons, double[] input, double[] output)
    {
        Array.Clear(output);
        foreach (Comparison comparison in comparisons)
        {
            double difference = comparison.Weight * (input[comparison.A] - input[comparison.B]);
            output[comparison.A] += difference;
            output[comparison.B] -= difference;
        }
        output[0] = input[0];
    }

    private static double[] SolveBradleyTerry(
        int vertexCount,
        Comparison[] comparisons,
        int iterations)
    {
        var strength = Enumerable.Repeat(1.0, vertexCount).ToArray();
        var wins = new double[vertexCount];
        foreach (Comparison comparison in comparisons)
        {
            wins[comparison.A] += comparison.WinsA;
            wins[comparison.B] += comparison.WinsB;
        }

        var denominator = new double[vertexCount];
        var next = new double[vertexCount];
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            Array.Clear(denominator);
            foreach (Comparison comparison in comparisons)
            {
                double term = (comparison.WinsA + comparison.WinsB) /
                    (strength[comparison.A] + strength[comparison.B]);
                denominator[comparison.A] += term;
                denominator[comparison.B] += term;
            }
            double sum = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                next[i] = Math.Max(1e-12, wins[i] / Math.Max(denominator[i], 1e-12));
                sum += next[i];
            }
            double scale = vertexCount / sum;
            for (int i = 0; i < vertexCount; i++)
                strength[i] = next[i] * scale;
        }

        for (int i = 0; i < strength.Length; i++)
            strength[i] = Math.Log(strength[i]);
        return strength;
    }

    private static double KendallTau(double[] expectedScores, double[] actualScores)
    {
        int n = expectedScores.Length;
        int[] expectedOrder = Enumerable.Range(0, n)
            .OrderBy(index => expectedScores[index])
            .ThenBy(index => index)
            .ToArray();
        int[] actualRank = new int[n];
        int[] actualOrder = Enumerable.Range(0, n)
            .OrderBy(index => actualScores[index])
            .ThenBy(index => index)
            .ToArray();
        for (int rank = 0; rank < n; rank++) actualRank[actualOrder[rank]] = rank;

        var fenwick = new FenwickTree(n);
        long inversions = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            int rank = actualRank[expectedOrder[i]];
            inversions += fenwick.Sum(rank);
            fenwick.Add(rank + 1, 1);
        }
        long pairs = (long)n * (n - 1) / 2;
        return pairs == 0 ? 1 : 1.0 - 2.0 * inversions / pairs;
    }

    private static double TriangleCurlRms(
        Dictionary<(int, int), double> edges,
        (int A, int B, int C)[] triangles)
    {
        double sumSquares = 0;
        foreach ((int a, int b, int c) in triangles)
        {
            double curl = Flow(edges, a, b) + Flow(edges, b, c) + Flow(edges, c, a);
            sumSquares += curl * curl;
        }
        return Math.Sqrt(sumSquares / triangles.Length);
    }

    private static double Flow(Dictionary<(int, int), double> edges, int from, int to)
        => from < to ? edges[(from, to)] : -edges[(to, from)];

    private static double NextGaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = 1.0 - random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static double Dot(double[] left, double[] right)
    {
        double sum = 0;
        for (int i = 0; i < left.Length; i++) sum += left[i] * right[i];
        return sum;
    }

    private static void Axpy(double[] target, double[] source, double scale)
    {
        for (int i = 0; i < target.Length; i++) target[i] += scale * source[i];
    }

    private readonly record struct Comparison(
        int A,
        int B,
        double Flow,
        int WinsA,
        int WinsB,
        double Weight);

    private readonly record struct HodgeSolution(double[] Scores, int Iterations, double RelativeResidual);

    private sealed class FenwickTree(int size)
    {
        private readonly long[] _tree = new long[size + 1];

        public void Add(int index, long value)
        {
            for (int i = index; i < _tree.Length; i += i & -i) _tree[i] += value;
        }

        public long Sum(int exclusiveIndex)
        {
            long sum = 0;
            for (int i = exclusiveIndex; i > 0; i -= i & -i) sum += _tree[i];
            return sum;
        }
    }
}
