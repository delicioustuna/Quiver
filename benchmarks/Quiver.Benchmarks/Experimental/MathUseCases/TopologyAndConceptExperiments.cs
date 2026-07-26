using System.Numerics;
using Quiver.Api;
using Quiver.Core;

namespace Quiver.Benchmarks.Experimental.MathUseCases;

internal static class TopologyAndConceptExperiments
{
    public static void Run()
    {
        Console.WriteLine("section=topology-and-concepts");
        VerifyFormalConceptEnumeration();
        CharacterizeConceptExplosion();
        VerifyZeroDimensionalPersistence();
        RecoverGaussianMixtureClusters();
        MeasureProductVectorAdapter();
        VerifyOneDimensionalPersistence();
        Console.WriteLine(
            "topology.vector-adapter source=explicit-candidate-traversal currentPublicLoop=REJECT indexLayerBatchGraph=REQUIRED privateHnswExposure=unnecessary");
    }

    private static void VerifyFormalConceptEnumeration()
    {
        var random = new Random(0x46434131);
        for (int trial = 0; trial < 40; trial++)
        {
            const int attributeCount = 9;
            ulong[] objects = new ulong[11];
            for (int obj = 0; obj < objects.Length; obj++)
            {
                ulong attributes = 0;
                for (int attribute = 0; attribute < attributeCount; attribute++)
                    if (random.NextDouble() < 0.42) attributes |= 1UL << attribute;
                objects[obj] = attributes;
            }

            ulong[] expected = BruteForceClosures(objects, attributeCount);
            ClosureEnumeration actual = EnumerateClosures(objects, attributeCount, int.MaxValue);
            SpikeCheck.SequenceEqual(expected, actual.Intents.Order().ToArray(),
                "NextClosure disagreed with exhaustive closure oracle");
        }
        Console.WriteLine("concepts.oracle trials=40 attributes=9 result=PASS");
    }

    private static void CharacterizeConceptExplosion()
    {
        const int dimension = 20;
        ulong all = (1UL << dimension) - 1;
        ulong[] objects = Enumerable.Range(0, dimension)
            .Select(attribute => all & ~(1UL << attribute))
            .ToArray();
        ClosureEnumeration capped = EnumerateClosures(objects, dimension, 100_000);
        SpikeCheck.True(capped.Truncated, "contranominal context did not reach the enumeration cap");

        long totalConcepts = 1L << dimension;
        long minSupportFour = 0;
        for (int intentSize = 0; intentSize <= dimension - 4; intentSize++)
            minSupportFour += Binomial(dimension, intentSize);
        double retained = minSupportFour / (double)totalConcepts;
        Console.WriteLine(
            $"concepts.explosion dimension={dimension} exactConcepts={totalConcepts} cap={capped.Intents.Length} " +
            $"minExtent4Concepts={minSupportFour} retainedPct={retained * 100:F2} " +
            "decision=min-support-plus-result-cap");
    }

    private static void VerifyZeroDimensionalPersistence()
    {
        Point[] points = CreateUniformPoints(220, 3, seed: 0x48304243);
        double[] exact = ZeroDimensionalDeaths(points.Length, CompleteEdges(points));
        int matchingK = -1;
        double[] approximate = [];
        for (int k = 2; k <= 40; k++)
        {
            approximate = ZeroDimensionalDeaths(points.Length, KnnEdges(points, k));
            if (!NearlyEqual(exact, approximate, 1e-12)) continue;
            matchingK = k;
            break;
        }
        SpikeCheck.True(matchingK > 0, "sparse k-NN graph did not preserve the exact H0 barcode");
        Console.WriteLine(
            $"topology.h0-oracle points={points.Length} dimension=3 minimumMatchingK={matchingK} finiteBars={exact.Length} result=PASS");
    }

    private static void RecoverGaussianMixtureClusters()
    {
        const int clusterCount = 3;
        Point[] points = CreateGaussianMixture(900, clusterCount, seed: 0x474d4d31);
        AlternatingSamples samples = SpikeMeasurement.Alternate(
            () => InferClusterCount(points, k: 16),
            () => InferClusterCount(points, k: 16),
            warmup: 1,
            samples: 7);
        SpikeCheck.Equal(clusterCount, (int)samples.First.Checksum,
            "largest persistence gap did not recover the planted clusters");
        Console.WriteLine(
            $"topology.gmm points={points.Length} clusters={clusterCount} inferred={samples.First.Checksum} " +
            $"medianMs={samples.First.MedianMilliseconds:F3} iqrPct={samples.First.IqrPercent:F2} " +
            $"allocBytes={samples.First.AllocatedBytes} result=PASS");
    }

    private static void VerifyOneDimensionalPersistence()
    {
        Point[] circle = Enumerable.Range(0, 16)
            .Select(i =>
            {
                double angle = 2 * Math.PI * i / 16;
                return new Point([Math.Cos(angle), Math.Sin(angle)]);
            })
            .ToArray();
        Point[] filled = [.. circle, new Point([0, 0])];

        PersistenceInterval[] circleIntervals = OneDimensionalIntervals(circle, maxScale: 1.5);
        PersistenceInterval[] filledIntervals = OneDimensionalIntervals(filled, maxScale: 1.5);
        int circleInfinite = circleIntervals.Count(static interval => double.IsPositiveInfinity(interval.Death));
        int filledInfinite = filledIntervals.Count(static interval => double.IsPositiveInfinity(interval.Death));
        SpikeCheck.Equal(1, circleInfinite, "circle did not expose one persistent H1 generator");
        SpikeCheck.Equal(0, filledInfinite, "coning the circle did not kill H1");

        double longestFinite = filledIntervals
            .Where(static interval => !double.IsPositiveInfinity(interval.Death))
            .Select(static interval => interval.Death - interval.Birth)
            .DefaultIfEmpty(0)
            .Max();
        Console.WriteLine(
            $"topology.h1 circleInfinite={circleInfinite} filledInfinite={filledInfinite} " +
            $"filledLongestFinite={longestFinite:F4} result=PASS");
    }

    private static void MeasureProductVectorAdapter()
    {
        const int pointCount = 10_000;
        const int dimensions = 384;
        const int clusterCount = 5;
        const int neighbors = 16;
        const string indexName = "topology_vectors";
        const string propertyName = "embedding";
        string directory = BenchTempDir.Create("math_topology_vectors");
        string path = Path.Combine(directory, "graph.quiver");
        try
        {
            using var db = QuiverDatabase.Open(path);
            db.EditSchema(schema =>
            {
                schema.GetOrCreatePropertyKey(propertyName);
                schema.CreateIndex(new VectorIndexDefinition(
                    indexName,
                    new PropertyTarget(PropertyOwnerKind.Vertex, propertyName, "Point"),
                    dimensions,
                    DistanceMetric.Euclidean,
                    HnswEfConstruction: 120));
            });

            var random = new Random(0x56454354);
            var vectors = new float[pointCount][];
            var owners = new EntityRef[pointCount];
            using (var write = db.BeginWriteTransaction())
            {
                for (int point = 0; point < pointCount; point++)
                {
                    int cluster = point % clusterCount;
                    var vector = new float[dimensions];
                    vector[0] = cluster * 40;
                    vector[1] = (cluster & 1) * 25;
                    for (int dimension = 0; dimension < dimensions; dimension++)
                        vector[dimension] += (float)(NextGaussian(random) * 0.08);
                    VertexId vertex = write.CreateVertex("Point");
                    write.SetVectorProperty(EntityRef.From(vertex), propertyName, vector);
                    vectors[point] = vector;
                    owners[point] = EntityRef.From(vertex);
                }
                write.Commit();
            }

            var ownerToPoint = owners.Select((owner, point) => (owner, point))
                .ToDictionary(static pair => pair.owner, static pair => pair.point);
            var edgeMap = new Dictionary<long, Edge>(pointCount * neighbors);
            using var read = db.BeginReadTransaction();
            long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            long graphStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var options = new VectorSearchOptions { EfSearch = 64 };
            for (int point = 0; point < pointCount; point++)
            {
                using VectorSearchCursor cursor = read.KnnSearch(indexName, vectors[point], neighbors + 1, options);
                while (cursor.MoveNext())
                {
                    VectorSearchResult hit = cursor.Current;
                    if (!ownerToPoint.TryGetValue(hit.Owner, out int other) || other == point) continue;
                    int lo = Math.Min(point, other);
                    int hi = Math.Max(point, other);
                    long key = ((long)lo << 32) | (uint)hi;
                    double distance = -hit.Score;
                    if (!edgeMap.TryGetValue(key, out Edge current) || distance < current.Weight)
                        edgeMap[key] = new Edge(lo, hi, distance);
                }
            }
            double graphMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(graphStart).TotalMilliseconds;

            long barcodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            double[] deaths = ZeroDimensionalDeaths(pointCount, edgeMap.Values.ToArray());
            double barcodeMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(barcodeStart).TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            int components = pointCount - deaths.Length;
            SpikeCheck.Equal(clusterCount, components,
                "product k-NN adapter did not preserve the separated cluster components");
            string coreGate = barcodeMilliseconds < 300 ? "PASS" : "ASPIRATIONAL_MISS";
            Console.WriteLine(
                $"topology.product-adapter points={pointCount} dimension={dimensions} k={neighbors} " +
                $"undirectedEdges={edgeMap.Count} components={components} graphMs={graphMilliseconds:F3} " +
                $"barcodeMs={barcodeMilliseconds:F3} allocBytes={allocated} coreGate={coreGate}");
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    private static ClosureEnumeration EnumerateClosures(ulong[] objects, int attributeCount, int cap)
    {
        ulong all = (1UL << attributeCount) - 1;
        var intents = new List<ulong>();
        ulong current = Closure(objects, 0, all).Intent;
        while (true)
        {
            intents.Add(current);
            if (intents.Count >= cap)
                return new ClosureEnumeration(intents.ToArray(), current != all);

            bool found = false;
            for (int attribute = attributeCount - 1; attribute >= 0; attribute--)
            {
                ulong bit = 1UL << attribute;
                if ((current & bit) != 0) continue;
                ulong lower = bit - 1;
                ulong candidateSeed = (current & lower) | bit;
                ulong candidate = Closure(objects, candidateSeed, all).Intent;
                if ((candidate & lower) != (current & lower)) continue;
                current = candidate;
                found = true;
                break;
            }
            if (!found) return new ClosureEnumeration(intents.ToArray(), false);
        }
    }

    private static ulong[] BruteForceClosures(ulong[] objects, int attributeCount)
    {
        ulong all = (1UL << attributeCount) - 1;
        var closures = new HashSet<ulong>();
        for (ulong attributes = 0; attributes <= all; attributes++)
            closures.Add(Closure(objects, attributes, all).Intent);
        return closures.Order().ToArray();
    }

    private static (ulong Intent, int Extent) Closure(ulong[] objects, ulong seed, ulong all)
    {
        ulong intent = all;
        int extent = 0;
        foreach (ulong obj in objects)
        {
            if ((obj & seed) != seed) continue;
            intent &= obj;
            extent++;
        }
        return (intent, extent);
    }

    private static long Binomial(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        k = Math.Min(k, n - k);
        long value = 1;
        for (int i = 1; i <= k; i++) value = checked(value * (n - k + i) / i);
        return value;
    }

    private static Point[] CreateUniformPoints(int count, int dimensions, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => new Point(Enumerable.Range(0, dimensions).Select(_ => random.NextDouble()).ToArray()))
            .ToArray();
    }

    private static Point[] CreateGaussianMixture(int count, int clusters, int seed)
    {
        var random = new Random(seed);
        var centers = new[]
        {
            new Point([-8.0, -4.0]),
            new Point([8.0, -4.0]),
            new Point([0.0, 9.0]),
        };
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                Point center = centers[i % clusters];
                return new Point(
                [
                    center.Coordinates[0] + NextGaussian(random) * 0.45,
                    center.Coordinates[1] + NextGaussian(random) * 0.45,
                ]);
            })
            .ToArray();
    }

    private static int InferClusterCount(Point[] points, int k)
    {
        double[] deaths = ZeroDimensionalDeaths(points.Length, KnnEdges(points, k));
        if (deaths.Length != points.Length - 1) return points.Length - deaths.Length;
        int largestGapIndex = 0;
        double largestGap = double.NegativeInfinity;
        for (int i = 0; i < deaths.Length - 1; i++)
        {
            double gap = deaths[i + 1] - deaths[i];
            if (gap <= largestGap) continue;
            largestGap = gap;
            largestGapIndex = i;
        }
        int mergeCountBeforeGap = largestGapIndex + 1;
        return points.Length - mergeCountBeforeGap;
    }

    private static Edge[] CompleteEdges(Point[] points)
    {
        var edges = new Edge[points.Length * (points.Length - 1) / 2];
        int index = 0;
        for (int a = 0; a < points.Length; a++)
            for (int b = a + 1; b < points.Length; b++)
                edges[index++] = new Edge(a, b, Distance(points[a], points[b]));
        return edges;
    }

    private static Edge[] KnnEdges(Point[] points, int k)
    {
        var edges = new Dictionary<long, Edge>();
        var bestVertices = new int[k];
        var bestDistances = new double[k];
        for (int a = 0; a < points.Length; a++)
        {
            Array.Fill(bestVertices, -1);
            Array.Fill(bestDistances, double.PositiveInfinity);
            for (int b = 0; b < points.Length; b++)
            {
                if (a == b) continue;
                double distance = Distance(points[a], points[b]);
                if (distance > bestDistances[^1]) continue;
                int position = k - 1;
                while (position > 0 &&
                    (distance < bestDistances[position - 1] ||
                     (distance == bestDistances[position - 1] && b < bestVertices[position - 1])))
                {
                    bestDistances[position] = bestDistances[position - 1];
                    bestVertices[position] = bestVertices[position - 1];
                    position--;
                }
                bestDistances[position] = distance;
                bestVertices[position] = b;
            }

            for (int i = 0; i < k && bestVertices[i] >= 0; i++)
            {
                int b = bestVertices[i];
                double distance = bestDistances[i];
                int lo = Math.Min(a, b);
                int hi = Math.Max(a, b);
                long key = ((long)lo << 32) | (uint)hi;
                if (!edges.TryGetValue(key, out Edge current) || distance < current.Weight)
                    edges[key] = new Edge(lo, hi, distance);
            }
        }
        return edges.Values.ToArray();
    }

    private static double[] ZeroDimensionalDeaths(int vertexCount, Edge[] edges)
    {
        Array.Sort(edges, static (left, right) =>
        {
            int weight = left.Weight.CompareTo(right.Weight);
            if (weight != 0) return weight;
            int a = left.A.CompareTo(right.A);
            return a != 0 ? a : left.B.CompareTo(right.B);
        });
        var unionFind = new UnionFind(vertexCount);
        var deaths = new List<double>(vertexCount - 1);
        foreach (Edge edge in edges)
            if (unionFind.Union(edge.A, edge.B)) deaths.Add(edge.Weight);
        return deaths.ToArray();
    }

    private static PersistenceInterval[] OneDimensionalIntervals(Point[] points, double maxScale)
    {
        var simplices = new List<Simplex>();
        for (int vertex = 0; vertex < points.Length; vertex++)
            simplices.Add(new Simplex(new SimplexKey(0, vertex, -1, -1), 0));

        var edgeWeights = new Dictionary<(int, int), double>();
        for (int a = 0; a < points.Length; a++)
        {
            for (int b = a + 1; b < points.Length; b++)
            {
                double distance = Distance(points[a], points[b]);
                if (distance > maxScale) continue;
                edgeWeights[(a, b)] = distance;
                simplices.Add(new Simplex(new SimplexKey(1, a, b, -1), distance));
            }
        }
        for (int a = 0; a < points.Length; a++)
        {
            for (int b = a + 1; b < points.Length; b++)
            {
                if (!edgeWeights.TryGetValue((a, b), out double ab)) continue;
                for (int c = b + 1; c < points.Length; c++)
                {
                    if (!edgeWeights.TryGetValue((a, c), out double ac) ||
                        !edgeWeights.TryGetValue((b, c), out double bc)) continue;
                    simplices.Add(new Simplex(new SimplexKey(2, a, b, c), Math.Max(ab, Math.Max(ac, bc))));
                }
            }
        }

        simplices.Sort(static (left, right) =>
        {
            int filtration = left.Filtration.CompareTo(right.Filtration);
            if (filtration != 0) return filtration;
            int dimension = left.Key.Dimension.CompareTo(right.Key.Dimension);
            if (dimension != 0) return dimension;
            int a = left.Key.A.CompareTo(right.Key.A);
            if (a != 0) return a;
            int b = left.Key.B.CompareTo(right.Key.B);
            return b != 0 ? b : left.Key.C.CompareTo(right.Key.C);
        });
        var index = simplices.Select((simplex, i) => (simplex.Key, i)).ToDictionary();
        var reducedByPivot = new Dictionary<int, HashSet<int>>();
        var positive = new bool[simplices.Count];
        var pairedBirths = new HashSet<int>();
        var intervals = new List<PersistenceInterval>();

        for (int columnIndex = 0; columnIndex < simplices.Count; columnIndex++)
        {
            Simplex simplex = simplices[columnIndex];
            var column = Boundary(simplex.Key, index);
            while (column.Count > 0)
            {
                int pivot = column.Max();
                if (!reducedByPivot.TryGetValue(pivot, out HashSet<int>? existing)) break;
                column.SymmetricExceptWith(existing);
            }

            if (column.Count == 0)
            {
                positive[columnIndex] = true;
                continue;
            }
            int low = column.Max();
            reducedByPivot[low] = column;
            pairedBirths.Add(low);
            if (simplices[low].Key.Dimension == 1)
                intervals.Add(new PersistenceInterval(simplices[low].Filtration, simplex.Filtration));
        }

        for (int i = 0; i < simplices.Count; i++)
            if (positive[i] && !pairedBirths.Contains(i) && simplices[i].Key.Dimension == 1)
                intervals.Add(new PersistenceInterval(simplices[i].Filtration, double.PositiveInfinity));
        return intervals.ToArray();
    }

    private static HashSet<int> Boundary(SimplexKey simplex, Dictionary<SimplexKey, int> index)
    {
        return simplex.Dimension switch
        {
            0 => [],
            1 => [
                index[new SimplexKey(0, simplex.A, -1, -1)],
                index[new SimplexKey(0, simplex.B, -1, -1)]],
            2 => [
                index[new SimplexKey(1, simplex.A, simplex.B, -1)],
                index[new SimplexKey(1, simplex.A, simplex.C, -1)],
                index[new SimplexKey(1, simplex.B, simplex.C, -1)]],
            _ => throw new ArgumentOutOfRangeException(nameof(simplex)),
        };
    }

    private static bool NearlyEqual(double[] left, double[] right, double tolerance)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
            if (Math.Abs(left[i] - right[i]) > tolerance) return false;
        return true;
    }

    private static double Distance(Point left, Point right)
    {
        double sum = 0;
        for (int i = 0; i < left.Coordinates.Length; i++)
        {
            double delta = left.Coordinates[i] - right.Coordinates[i];
            sum += delta * delta;
        }
        return Math.Sqrt(sum);
    }

    private static double NextGaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = 1.0 - random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private readonly record struct ClosureEnumeration(ulong[] Intents, bool Truncated);
    private readonly record struct Point(double[] Coordinates);
    private readonly record struct Edge(int A, int B, double Weight);
    private readonly record struct PersistenceInterval(double Birth, double Death);
    private readonly record struct Simplex(SimplexKey Key, double Filtration);
    private readonly record struct SimplexKey(int Dimension, int A, int B, int C);

    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;

        public UnionFind(int count)
        {
            _parent = Enumerable.Range(0, count).ToArray();
            _rank = new byte[count];
        }

        public bool Union(int left, int right)
        {
            int leftRoot = Find(left);
            int rightRoot = Find(right);
            if (leftRoot == rightRoot) return false;
            if (_rank[leftRoot] < _rank[rightRoot]) (leftRoot, rightRoot) = (rightRoot, leftRoot);
            _parent[rightRoot] = leftRoot;
            if (_rank[leftRoot] == _rank[rightRoot]) _rank[leftRoot]++;
            return true;
        }

        private int Find(int value)
        {
            while (_parent[value] != value)
            {
                _parent[value] = _parent[_parent[value]];
                value = _parent[value];
            }
            return value;
        }
    }
}
