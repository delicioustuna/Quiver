using System.Diagnostics;
using Quiver;
using Quiver.Api;
using Quiver.Api.Match;
using Quiver.Core;

namespace Quiver.Benchmarks;

/// <summary>
/// 四つの役割を一つの関係に束ねる星型パターンの取得コストを、
/// 第一級ハイパーエッジの <see cref="GraphPattern.Hyperedge(string, string?)"/> Match と、
/// 同じ事実をノード + メンバーリレーションシップで具象化 (reify) した多段結合とで比較する。
/// </summary>
/// <remarks>
/// どちらも一つの事実あたり subject / object / source / asOf の 4 メンバーを 1 行に束ねる。
/// 星型 Match は最初のメンバーを anchor に label scan → node→hyperedge → 残りメンバー展開へ落ち、
/// reified 側は Fact ノードを起点に 4 本のリレーションシップを結合する。数値ゲートは無く、
/// 実測 p50 と比率を記録する。
/// </remarks>
public static class HyperedgeMatchBenchmarks
{
    private const int FactCount = 1_000;
    private const int Warmup = 50;
    private const int Iterations = 300;

    public static int Run()
    {
        Console.WriteLine("=== Star hyperedge Match vs. reified graph pattern (product API) ===");
        Console.WriteLine($"facts={FactCount}  warmup={Warmup}  iterations={Iterations}");
        Console.WriteLine();

        string directory = BenchTempDir.Create("hyperedge_match");
        Directory.CreateDirectory(directory);
        try
        {
            using var db = GraphDatabase.Open(Path.Combine(directory, "graph.quiver"));
            BuildDataset(db);

            using var tx = db.BeginReadOnlyTransaction();
            var g = tx.G(db.Schema);

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

    // 同じ事実集合を二重に持たせる。ハイパーエッジ型 "Fact" (4 ロール) と、
    // ノードラベル "Fact" を中心に 4 本のロール名リレーションシップを張った reified 表現。
    // ハイパーエッジ型名とノードラベル名は別空間なので衝突しない。
    private static void BuildDataset(GraphDatabase db)
    {
        using var tx = db.BeginTransaction();
        NodeId asOf = tx.CreateNode("TimePoint");

        for (int i = 0; i < FactCount; i++)
        {
            NodeId subject = tx.CreateNode("Subject");
            NodeId obj = tx.CreateNode("Object");
            NodeId source = tx.CreateNode("Chunk");

            tx.CreateHyperedge("Fact",
            [
                new HyperedgeMember("subject", subject),
                new HyperedgeMember("object", obj),
                new HyperedgeMember("source", source),
                new HyperedgeMember("asOf", asOf),
            ]);

            NodeId factNode = tx.CreateNode("Fact");
            tx.CreateRelationship(factNode, subject, "subject");
            tx.CreateRelationship(factNode, obj, "object");
            tx.CreateRelationship(factNode, source, "source");
            tx.CreateRelationship(factNode, asOf, "asOf");
        }

        tx.Commit();
    }

    private static long StarMatch(GraphTraversalSource g)
    {
        var pattern = GraphPattern.Hyperedge("f", "Fact")
            .Member("subject", GraphPattern.Node("s", "Subject"))
            .Member("object", GraphPattern.Node("o", "Object"))
            .Member("source", GraphPattern.Node("src", "Chunk"))
            .Member("asOf", GraphPattern.Node("t", "TimePoint"));
        return g.Match(pattern).Count();
    }

    private static long ReifiedJoin(GraphTraversalSource g)
        => g.Nodes().HasLabel("Fact").As("f")
            .Out("subject").As("s")
            .Select<NodeId>("f")
            .Out("object").As("o")
            .Select<NodeId>("f")
            .Out("source").As("src")
            .Select<NodeId>("f")
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
