using System.Diagnostics;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Benchmarks;

/// <summary>
/// 製品 API (fluent traversal) を通した 1-hop 走査の p50 比較。
/// binary リレーションシップの 1-hop (<c>g.Node(hub).Out(...)</c>) と、
/// 役割を指定した co-membership 展開
/// (<c>g.Node(hub).Hyperedges("Fact", "subject").OtherMembers("object")</c>) を、
/// 次数 10 / 100 / 1,000・アリティ 4 で計測する。
/// </summary>
/// <remarks>
/// co-membership 側は、起点と取得の両ロールを物理化した導出ビュー経由と、
/// ビュー未設定時にフォールバックする incidence リンクチェーン経由の両方を測る。
/// 判定対象はビュー経路 (製品が既定で提供する co-membership view) が
/// binary の 3 倍以内に収まるかである。
/// </remarks>
public static class HyperedgeTraversalBenchmarks
{
    private const int Warmup = 300;
    private const int Iterations = 3_000;
    // ビュー経路が binary 1-hop に対して許容される p50 の上限倍率。
    private const double RatioGate = 3.0;

    private static readonly int[] Degrees = [10, 100, 1_000];

    public static int Run()
    {
        Console.WriteLine("=== Co-membership traversal vs. binary 1-hop (product API) ===");
        Console.WriteLine($"warmup={Warmup}  iterations={Iterations}  arity=4  gate=<={RatioGate:F1}x binary");
        Console.WriteLine();
        Console.WriteLine(
            $"{"Degree",8} {"Binary us",10} {"View us",10} {"View x",8} " +
            $"{"Chain us",10} {"Chain x",8} {"Bin alloc",10} {"View alloc",10} {"Gate",6}");
        Console.WriteLine(new string('-', 92));

        bool allPass = true;
        foreach (int degree in Degrees)
            allPass &= RunDegree(degree);

        Console.WriteLine();
        Console.WriteLine($"overall traversal gate: {(allPass ? "PASS" : "FAIL")}");
        return allPass ? 0 : 1;
    }

    private static bool RunDegree(int degree)
    {
        string directory = BenchTempDir.Create($"hyperedge_traversal_d{degree}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "graph.quiver");

        try
        {
            NodeId hub = BuildDataset(databasePath, degree);

            double binaryP50, viewP50;
            long binaryAlloc, viewAlloc;
            // ビュー登録済みでオープンし、binary と co-membership view を交互に測る。
            using (var db = OpenWithView(databasePath))
            {
                AssertViewEngaged(db);
                using var tx = db.BeginReadOnlyTransaction();
                var g = tx.G(db.Schema);

                binaryP50 = MeasureP50(() => g.Node(hub).Out("Link").Count(), degree);
                viewP50 = MeasureP50(
                    () => g.Node(hub).Hyperedges("Fact", "subject").OtherMembers("object").Count(),
                    degree);
                binaryAlloc = MeasureAlloc(() => g.Node(hub).Out("Link").Count());
                viewAlloc = MeasureAlloc(
                    () => g.Node(hub).Hyperedges("Fact", "subject").OtherMembers("object").Count());
            }

            // ビュー未登録でオープンすると同じクエリが incidence リンクチェーンへ
            // フォールバックする。ビューがどれだけ固定費を削るかの対照値。
            double chainP50;
            using (var db = GraphDatabase.Open(databasePath))
            {
                using var tx = db.BeginReadOnlyTransaction();
                var g = tx.G(db.Schema);
                chainP50 = MeasureP50(
                    () => g.Node(hub).Hyperedges("Fact", "subject").OtherMembers("object").Count(),
                    degree);
            }

            double viewRatio = viewP50 / binaryP50;
            double chainRatio = chainP50 / binaryP50;
            bool pass = viewRatio <= RatioGate;

            Console.WriteLine(
                $"{degree,8} {binaryP50 / 1000.0,10:F3} {viewP50 / 1000.0,10:F3} {viewRatio,7:F2}x " +
                $"{chainP50 / 1000.0,10:F3} {chainRatio,7:F2}x {binaryAlloc,10} {viewAlloc,10} " +
                $"{(pass ? "PASS" : "FAIL"),6}");
            return pass;
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    // hub は degree 個の binary リレーションシップの起点であり、かつ degree 個の
    // arity-4 ハイパーエッジで subject ロールを担う。各ハイパーエッジの object は
    // 固有ノード、source / asOf は全件で共有する (co-membership 対象は subject→object)。
    private static NodeId BuildDataset(string databasePath, int degree)
    {
        using var db = OpenWithView(databasePath);
        using var tx = db.BeginTransaction();

        NodeId hub = tx.CreateNode("Hub");
        NodeId source = tx.CreateNode("Chunk");
        NodeId asOf = tx.CreateNode("TimePoint");

        for (int i = 0; i < degree; i++)
        {
            NodeId obj = tx.CreateNode("Object");
            tx.CreateRelationship(hub, obj, "Link");
            tx.CreateHyperedge("Fact",
            [
                new HyperedgeMember("subject", hub),
                new HyperedgeMember("object", obj),
                new HyperedgeMember("source", source),
                new HyperedgeMember("asOf", asOf),
            ]);
        }

        tx.Commit();
        return hub;
    }

    private static GraphDatabase OpenWithView(string databasePath)
    {
        var options = new GraphDatabaseOptions();
        options.CoMembershipRolePairs.Add(new CoMembershipRolePair("subject", "object"));
        return GraphDatabase.Open(databasePath, options);
    }

    // planner は両ロール指定の OtherMembers を CoMembershipOperator へ落とすが、
    // その operator が導出ビューを使うのは
    // tx.CoMembershipBlocks.Contains(originRole, memberRole) が真のときだけである。
    // ここで同じ条件を直接確認し、ビュー経路が実際に選ばれることを保証する。
    private static void AssertViewEngaged(GraphDatabase db)
    {
        using var tx = db.BeginReadOnlyTransaction();
        ICoMembershipBlockStore? view = ((GraphTransaction)tx).Inner.CoMembershipBlocks;
        var resolver = (IHyperedgeSchemaResolver)db.Schema;
        resolver.TryGetRoleId("subject", out RoleId subjectRole);
        resolver.TryGetRoleId("object", out RoleId objectRole);
        if (view is null || !view.Contains(subjectRole, objectRole))
            throw new InvalidOperationException(
                "Co-membership view was not engaged; the traversal would fall back to the incidence chain.");
    }

    private static double MeasureP50(Func<long> op, int expected)
    {
        for (int i = 0; i < Warmup; i++)
        {
            if (op() != expected)
                throw new InvalidOperationException($"traversal count != {expected}");
        }

        var samples = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            long count = op();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
            if (count != expected)
                throw new InvalidOperationException($"traversal count {count} != {expected}");
        }

        Array.Sort(samples);
        return samples[Iterations / 2];
    }

    private static long MeasureAlloc(Func<long> op)
    {
        const int rounds = 200;
        for (int i = 0; i < 20; i++)
            op();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < rounds; i++)
            op();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / rounds;
    }
}
