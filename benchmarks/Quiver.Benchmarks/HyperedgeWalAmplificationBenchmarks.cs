using Quiver.Api;
using Quiver.Core;

namespace Quiver.Benchmarks;

/// <summary>
/// hyperedge create の WAL bytes が arity に対して線形かを判定する
/// standalone spike。token 作成と初回 page allocation は warm-up 後の checkpoint で
/// 計測区間から除外する。
/// </summary>
public static class HyperedgeWalAmplificationBenchmarks
{
    public readonly record struct Result(
        string Entity,
        int Arity,
        int ItemCount,
        long WalBytes,
        double WalBytesPerItem);

    private static readonly int[] Arities = [2, 4, 8, 16];

    public static int Run()
    {
        Console.WriteLine("=== HYP-2c: Hyperedge WAL amplification ===");
        Console.WriteLine("entity,arity,itemCount,walBytes,walBytesPerItem,ratioToBinary,limit,pass");

        foreach (int itemCount in new[] { 1, 1_000 })
        {
            Result binary = MeasureRelationship(itemCount);
            Console.WriteLine(
                $"relationship,2,{itemCount},{binary.WalBytes},{binary.WalBytesPerItem:F2},1.000,1.000,true");

            var hyperedges = new Result[Arities.Length];
            for (int i = 0; i < Arities.Length; i++)
            {
                int arity = Arities[i];
                Result result = MeasureHyperedge(arity, itemCount);
                hyperedges[i] = result;

                double ratio = result.WalBytes / (double)binary.WalBytes;
                double limit = 1 + arity / 2d;
                Console.WriteLine(
                    $"hyperedge,{arity},{itemCount},{result.WalBytes},{result.WalBytesPerItem:F2}," +
                    $"{ratio:F3},{limit:F3},{ratio <= limit}");
            }

            double rSquared = LinearRSquared(hyperedges);
            Console.WriteLine($"linearity,itemCount={itemCount},rSquared={rSquared:F6},pass={rSquared >= 0.98}");

            Result withView = MeasureHyperedge(4, itemCount, useCoMembershipView: true);
            Result withoutView = hyperedges[1];
            double viewRatio = withView.WalBytes / (double)withoutView.WalBytes;
            Console.WriteLine(
                $"coMembershipView,4,{itemCount},{withView.WalBytes},{withView.WalBytesPerItem:F2}," +
                $"ratioToBase={viewRatio:F3}," +
                $"pass={viewRatio <= 1.01}");
        }

        return 0;
    }

    internal static Result MeasureRelationship(int itemCount)
    {
        string directory = BenchTempDir.Create("hyp2c_rel");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "graph.quiver");

        try
        {
            using var db = OpenForMeasurement(databasePath);
            NodeId[] nodes = CreateNodes(db, 16);

            using (var warmup = db.BeginTransaction())
            {
                warmup.CreateRelationship(nodes[0], nodes[1], "Link");
                warmup.Commit();
            }

            Checkpoint(db, directory);
            string walPath = databasePath + "-wal";
            long before = FileLength(walPath);

            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < itemCount; i++)
                    tx.CreateRelationship(nodes[0], nodes[1], "Link");
                tx.Commit();
            }

            long bytes = FileLength(walPath) - before;
            return new Result("relationship", 2, itemCount, bytes, bytes / (double)itemCount);
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    internal static Result MeasureHyperedge(
        int arity,
        int itemCount,
        bool useCoMembershipView = false)
    {
        if (Array.IndexOf(Arities, arity) < 0)
            throw new ArgumentOutOfRangeException(nameof(arity));

        string directory = BenchTempDir.Create($"hyp2c_he{arity}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "graph.quiver");

        try
        {
            using var db = OpenForMeasurement(databasePath, useCoMembershipView);
            NodeId[] nodes = CreateNodes(db, 16);
            var members = new HyperedgeMember[arity];
            for (int i = 0; i < members.Length; i++)
                members[i] = new HyperedgeMember($"Role{i}", nodes[i]);

            using (var warmup = db.BeginTransaction())
            {
                warmup.CreateHyperedge("Fact", members);
                warmup.Commit();
            }

            Checkpoint(db, directory);
            string walPath = databasePath + "-wal";
            long before = FileLength(walPath);

            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < itemCount; i++)
                    tx.CreateHyperedge("Fact", members);
                tx.Commit();
            }

            long bytes = FileLength(walPath) - before;
            return new Result("hyperedge", arity, itemCount, bytes, bytes / (double)itemCount);
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    internal static double LinearRSquared(ReadOnlySpan<Result> results)
    {
        double meanX = 0;
        double meanY = 0;
        for (int i = 0; i < results.Length; i++)
        {
            meanX += results[i].Arity;
            meanY += results[i].WalBytesPerItem;
        }
        meanX /= results.Length;
        meanY /= results.Length;

        double covariance = 0;
        double varianceX = 0;
        for (int i = 0; i < results.Length; i++)
        {
            double dx = results[i].Arity - meanX;
            covariance += dx * (results[i].WalBytesPerItem - meanY);
            varianceX += dx * dx;
        }

        double slope = covariance / varianceX;
        double intercept = meanY - slope * meanX;
        double residual = 0;
        double total = 0;
        for (int i = 0; i < results.Length; i++)
        {
            double observed = results[i].WalBytesPerItem;
            double predicted = intercept + slope * results[i].Arity;
            residual += (observed - predicted) * (observed - predicted);
            total += (observed - meanY) * (observed - meanY);
        }
        return total == 0 ? 1 : 1 - residual / total;
    }

    private static GraphDatabase OpenForMeasurement(
        string databasePath,
        bool useCoMembershipView = false)
    {
        var options = new GraphDatabaseOptions
        {
            // 計測区間内の自動 checkpoint/truncate を止め、WAL file length の差分を
            // IWriteAheadLog.BytesWritten の差分と一致させる。
            CheckpointThresholdBytes = 0,
        };
        if (useCoMembershipView)
            options.CoMembershipRolePairs.Add(new("Role0", "Role1"));
        return GraphDatabase.Open(databasePath, options);
    }

    private static NodeId[] CreateNodes(GraphDatabase db, int count)
    {
        var nodes = new NodeId[count];
        using var tx = db.BeginTransaction();
        for (int i = 0; i < nodes.Length; i++)
            nodes[i] = tx.CreateNode("Member");
        tx.Commit();
        return nodes;
    }

    private static void Checkpoint(GraphDatabase db, string directory)
    {
        string snapshot = Path.Combine(directory, "checkpoint.quiver");
        db.CreateSnapshot(snapshot);
        File.Delete(snapshot);
        File.Delete(snapshot + "-wal");
    }

    private static long FileLength(string path)
        => File.Exists(path) ? new FileInfo(path).Length : 0;
}
