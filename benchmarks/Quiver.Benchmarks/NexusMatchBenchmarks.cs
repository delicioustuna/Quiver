using System.Diagnostics;
using Quiver;
using Quiver.Api;
using Quiver.Api.Match;
using Quiver.Core;

namespace Quiver.Benchmarks;

/// <summary>
/// 四つの役割を一つの関係に束ねる星型パターンの取得コストを、
/// 第一級Nexusの <see cref="GraphPattern.Nexus(string, string?)"/> Match と、
/// 同じ事実をVertex + メンバーEdgeで具象化 (reify) した多段結合とで比較する。
/// </summary>
/// <remarks>
/// どちらも一つの事実あたり subject / object / source / asOf の 4 メンバーを 1 行に束ねる。
/// 星型 Match は最初のメンバーを anchor に label scan → vertex→nexus → 残りメンバー展開へ落ち、
/// reified 側は Fact Vertexを起点に 4 本のEdgeを結合する。数値ゲートは無く、
/// 実測 p50 と比率を記録する。
/// </remarks>
public static class NexusMatchBenchmarks
{
    private const int FactCount = 1_000;
    private const int Warmup = 50;
    private const int Iterations = 300;

    public static int Run()
    {
        Console.WriteLine("=== Star nexus Match vs. reified graph pattern (product API) ===");
        Console.WriteLine($"facts={FactCount}  warmup={Warmup}  iterations={Iterations}");
        Console.WriteLine();

        string directory = BenchTempDir.Create("nexus_match");
        Directory.CreateDirectory(directory);
        try
        {
            using var db = QuiverDatabase.Open(Path.Combine(directory, "graph.quiver"));
            BuildDataset(db);

            using var tx = db.BeginReadTransaction();
            var g = tx.Query;

            double starP50 = MeasureP50(() => StarMatch(g), FactCount);
            double reifiedP50 = MeasureP50(() => ReifiedJoin(g), FactCount);

            Console.WriteLine($"{"Pattern",16} {"rows",8} {"p50 ms",10}");
            Console.WriteLine(new string('-', 38));
            Console.WriteLine($"{"star Match",16} {FactCount,8} {starP50 / 1_000_000.0,10:F3}");
            Console.WriteLine($"{"reified join",16} {FactCount,8} {reifiedP50 / 1_000_000.0,10:F3}");
            Console.WriteLine();
            Console.WriteLine($"ratio (star / reified): {starP50 / reifiedP50:F2}x");
            return 0;
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    // 同じ事実集合を二重に持たせる。Nexus型 "Fact" (4 ロール) と、
    // Vertexラベル "Fact" を中心に 4 本のロール名Edgeを張った reified 表現。
    // Nexus型名とVertexラベル名は別空間なので衝突しない。
    private static void BuildDataset(QuiverDatabase db)
    {
        using var tx = db.BeginWriteTransaction();
        VertexId asOf = tx.CreateVertex("TimePoint");

        for (int i = 0; i < FactCount; i++)
        {
            VertexId subject = tx.CreateVertex("Subject");
            VertexId obj = tx.CreateVertex("Object");
            VertexId source = tx.CreateVertex("Chunk");

            tx.CreateNexus("Fact",
            [
                new NexusMember("subject", subject),
                new NexusMember("object", obj),
                new NexusMember("source", source),
                new NexusMember("asOf", asOf),
            ]);

            VertexId factVertex = tx.CreateVertex("Fact");
            tx.CreateEdge(factVertex, subject, "subject");
            tx.CreateEdge(factVertex, obj, "object");
            tx.CreateEdge(factVertex, source, "source");
            tx.CreateEdge(factVertex, asOf, "asOf");
        }

        tx.Commit();
    }

    private static long StarMatch(GraphTraversalSource g)
    {
        var pattern = GraphPattern.Nexus("f", "Fact")
            .Member("subject", GraphPattern.Vertex("s", "Subject"))
            .Member("object", GraphPattern.Vertex("o", "Object"))
            .Member("source", GraphPattern.Vertex("src", "Chunk"))
            .Member("asOf", GraphPattern.Vertex("t", "TimePoint"));
        return g.Match(pattern).Count();
    }

    private static long ReifiedJoin(GraphTraversalSource g)
        => g.Vertices().HasLabel("Fact").As("f")
            .Out("subject").As("s")
            .Select<VertexId>("f")
            .Out("object").As("o")
            .Select<VertexId>("f")
            .Out("source").As("src")
            .Select<VertexId>("f")
            .Out("asOf").As("t")
            .Count();

    private static double MeasureP50(Func<long> op, int expected)
    {
        for (int i = 0; i < Warmup; i++)
        {
            if (op() != expected)
                throw new InvalidOperationException($"match count != {expected}");
        }

        var samples = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            long count = op();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
            if (count != expected)
                throw new InvalidOperationException($"match count {count} != {expected}");
        }

        Array.Sort(samples);
        return samples[Iterations / 2];
    }
}
