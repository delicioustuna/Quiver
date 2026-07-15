using System.Diagnostics;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Benchmarks;

/// <summary>
/// 製品 API を通したハイパーエッジ書き込みの費用を計測する。
/// アリティ 2 / 4 / 8 / 16 について、作成・プロパティ書き込み・削除の
/// 1 件あたり遅延と、作成の WAL バイト増幅 (binary リレーションシップ比) を測る。
/// さらに、1 ノードが 10^3〜10^4 のハイパーエッジのメンバーである状態の
/// <c>DeleteNode</c> カスケードのトランザクション時間・WAL・削除後整合性を記録する。
/// </summary>
/// <remarks>
/// WAL 増幅の判定は binary リレーションシップ作成比 <c>(1 + arity / 2)</c> 倍以内。
/// 高次数カスケードは数値ゲートを置かず、実測値と挙動 (デッドロックしないこと、
/// 削除後の整合性検査が 0 件) を記録する。
/// </remarks>
public static class HyperedgeWriteBenchmarks
{
    private static readonly int[] Arities = [2, 4, 8, 16];
    private static readonly int[] CascadeDegrees = [1_000, 10_000];

    // 遅延計測の 1 トランザクションあたりの操作件数。commit 費用を十分に償却する。
    private const int LatencyOps = 2_000;
    // WAL 計測のバッチ件数。固定費が償却され限界費用 (member あたり) が見える。
    private const int WalBatch = 1_000;

    public static int Run()
    {
        Console.WriteLine("=== Hyperedge write costs (product API) ===");
        Console.WriteLine(
            $"latencyOps={LatencyOps}/tx  walBatch={WalBatch}  " +
            "walGate=<=(1+arity/2)x binary relationship");
        Console.WriteLine();

        long binaryWal = MeasureRelationshipCreateWal(WalBatch);
        Console.WriteLine(
            $"binary relationship create WAL: {binaryWal} bytes " +
            $"({binaryWal / (double)WalBatch:F2}/item)");
        Console.WriteLine();

        Console.WriteLine(
            $"{"Arity",6} {"create us",10} {"setProp us",11} {"delete us",10} " +
            $"{"createWAL",10} {"bytes/item",11} {"ratio",7} {"limit",7} {"Gate",6}");
        Console.WriteLine(new string('-', 92));

        bool allPass = true;
        foreach (int arity in Arities)
        {
            double createUs = MeasureCreateLatency(arity);
            double propUs = MeasurePropertyLatency(arity);
            double deleteUs = MeasureDeleteLatency(arity);
            long createWal = MeasureHyperedgeCreateWal(arity, WalBatch);

            double bytesPerItem = createWal / (double)WalBatch;
            double ratio = createWal / (double)binaryWal;
            double limit = 1 + arity / 2.0;
            bool pass = ratio <= limit;
            allPass &= pass;

            Console.WriteLine(
                $"{arity,6} {createUs,10:F3} {propUs,11:F3} {deleteUs,10:F3} " +
                $"{createWal,10} {bytesPerItem,11:F2} {ratio,6:F3}x {limit,6:F3}x " +
                $"{(pass ? "PASS" : "FAIL"),6}");
        }

        Console.WriteLine();
        Console.WriteLine($"WAL amplification gate: {(allPass ? "PASS" : "FAIL")}");
        Console.WriteLine();

        Console.WriteLine("High-degree DeleteNode cascade:");
        Console.WriteLine(
            $"{"Degree",8} {"deleteTx ms",12} {"WAL bytes",12} {"consistent",11} {"issues",7}");
        Console.WriteLine(new string('-', 56));
        foreach (int degree in CascadeDegrees)
            RunDeleteNodeCascade(degree);

        return allPass ? 0 : 1;
    }

    // ── 遅延計測 ───────────────────────────────────────────────────────────

    private static double MeasureCreateLatency(int arity)
    {
        string directory = BenchTempDir.Create($"hyperedge_create{arity}");
        Directory.CreateDirectory(directory);
        try
        {
            using var db = GraphDatabase.Open(Path.Combine(directory, "graph.quiver"));
            NodeId[] nodes = CreateNodes(db, arity);
            HyperedgeMember[] members = BuildMembers(arity, nodes);

            // warm-up トランザクションで JIT と初回 page allocation を除外する。
            CommitCreates(db, members, LatencyOps);

            long start = Stopwatch.GetTimestamp();
            CommitCreates(db, members, LatencyOps);
            double elapsedUs = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            return elapsedUs / LatencyOps;
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    private static double MeasurePropertyLatency(int arity)
    {
        string directory = BenchTempDir.Create($"hyperedge_prop{arity}");
        Directory.CreateDirectory(directory);
        try
        {
            using var db = GraphDatabase.Open(Path.Combine(directory, "graph.quiver"));
            NodeId[] nodes = CreateNodes(db, arity);
            HyperedgeMember[] members = BuildMembers(arity, nodes);
            HyperedgeId[] ids = CommitCreates(db, members, LatencyOps);

            var value = PropertyValue.FromString("scored");
            // warm-up: 同じ inline スロットへ 1 巡書いておく。
            WriteProperty(db, ids, value);

            long start = Stopwatch.GetTimestamp();
            WriteProperty(db, ids, value);
            double elapsedUs = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            return elapsedUs / ids.Length;
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    private static double MeasureDeleteLatency(int arity)
    {
        string directory = BenchTempDir.Create($"hyperedge_delete{arity}");
        Directory.CreateDirectory(directory);
        try
        {
            using var db = GraphDatabase.Open(Path.Combine(directory, "graph.quiver"));
            NodeId[] nodes = CreateNodes(db, arity);
            HyperedgeMember[] members = BuildMembers(arity, nodes);
            HyperedgeId[] ids = CommitCreates(db, members, LatencyOps);

            long start = Stopwatch.GetTimestamp();
            using (var tx = db.BeginTransaction())
            {
                foreach (HyperedgeId id in ids)
                    tx.DeleteHyperedge(id);
                tx.Commit();
            }
            double elapsedUs = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            return elapsedUs / ids.Length;
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    // ── WAL 増幅 ───────────────────────────────────────────────────────────

    private static long MeasureHyperedgeCreateWal(int arity, int itemCount)
    {
        string directory = BenchTempDir.Create($"hyperedge_createwal{arity}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "graph.quiver");
        try
        {
            using var db = OpenForWalMeasurement(databasePath);
            NodeId[] nodes = CreateNodes(db, arity);
            HyperedgeMember[] members = BuildMembers(arity, nodes);

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

            return FileLength(walPath) - before;
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    private static long MeasureRelationshipCreateWal(int itemCount)
    {
        string directory = BenchTempDir.Create("hyperedge_relwal");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "graph.quiver");
        try
        {
            using var db = OpenForWalMeasurement(databasePath);
            NodeId[] nodes = CreateNodes(db, 2);

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

            return FileLength(walPath) - before;
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    // ── 高次数 DeleteNode カスケード ────────────────────────────────────────

    // hub は degree 個のハイパーエッジすべてで subject を担う。DeleteNode は所属する
    // 全ハイパーエッジを同一トランザクションで削除する契約なので、その tx 時間・WAL・
    // 削除後の整合性を測る。RAG では Chunk や頻出エンティティが高次数になりうる。
    private static void RunDeleteNodeCascade(int degree)
    {
        string directory = BenchTempDir.Create($"hyperedge_cascade{degree}");
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "graph.quiver");
        try
        {
            using var db = OpenForWalMeasurement(databasePath);

            NodeId hub;
            using (var tx = db.BeginTransaction())
            {
                hub = tx.CreateNode("Hub");
                NodeId obj = tx.CreateNode("Object");
                NodeId source = tx.CreateNode("Chunk");
                NodeId asOf = tx.CreateNode("TimePoint");
                for (int i = 0; i < degree; i++)
                {
                    tx.CreateHyperedge("Fact",
                    [
                        new HyperedgeMember("subject", hub),
                        new HyperedgeMember("object", obj),
                        new HyperedgeMember("source", source),
                        new HyperedgeMember("asOf", asOf),
                    ]);
                }
                tx.Commit();
            }

            Checkpoint(db, directory);
            string walPath = databasePath + "-wal";
            long before = FileLength(walPath);

            long start = Stopwatch.GetTimestamp();
            using (var tx = db.BeginTransaction())
            {
                tx.DeleteNode(hub);
                tx.Commit();
            }
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            long walBytes = FileLength(walPath) - before;

            ConsistencyReport report = db.Diagnostics.CheckConsistency();

            Console.WriteLine(
                $"{degree,8} {elapsedMs,12:F2} {walBytes,12} " +
                $"{report.IsConsistent,11} {report.Issues.Count,7}");
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    // ── 共通ヘルパ ─────────────────────────────────────────────────────────

    private static NodeId[] CreateNodes(GraphDatabase db, int count)
    {
        var nodes = new NodeId[count];
        using var tx = db.BeginTransaction();
        for (int i = 0; i < nodes.Length; i++)
            nodes[i] = tx.CreateNode("Member");
        tx.Commit();
        return nodes;
    }

    private static HyperedgeMember[] BuildMembers(int arity, NodeId[] nodes)
    {
        var members = new HyperedgeMember[arity];
        for (int i = 0; i < arity; i++)
            members[i] = new HyperedgeMember($"Role{i}", nodes[i]);
        return members;
    }

    private static HyperedgeId[] CommitCreates(GraphDatabase db, HyperedgeMember[] members, int count)
    {
        var ids = new HyperedgeId[count];
        using var tx = db.BeginTransaction();
        for (int i = 0; i < count; i++)
            ids[i] = tx.CreateHyperedge("Fact", members);
        tx.Commit();
        return ids;
    }

    private static void WriteProperty(GraphDatabase db, HyperedgeId[] ids, PropertyValue value)
    {
        using var tx = db.BeginTransaction();
        foreach (HyperedgeId id in ids)
            tx.SetProperty(id, "score", in value);
        tx.Commit();
    }

    private static GraphDatabase OpenForWalMeasurement(string databasePath)
    {
        // 計測区間内の自動 checkpoint/truncate を止め、WAL file length 差分を
        // 実際の書き込み量に一致させる。
        var options = new GraphDatabaseOptions { CheckpointThresholdBytes = 0 };
        return GraphDatabase.Open(databasePath, options);
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
