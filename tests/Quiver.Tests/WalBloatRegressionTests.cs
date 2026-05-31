using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 案A (checkpoint+truncate 配線) + 案C (TX 内ページイメージ・コアレス) の回帰センチネル。
///
/// FilterChainExpandBenchmarks の GlobalSetup で WAL が単発 160 GB に肥大した不具合の再発防止。
/// 根本原因は (1) UnpinDirty が毎回フルページ 8KB を WAL に追記し、(2) checkpoint/truncate が
/// production から一度も呼ばれず、(3) FlushMeta() がエッジ作成ごとに同一ヘッダページを再ログ
/// していたこと。案C で 1 トランザクション内の同一ページを最新版 1 件に畳み込み、案A で
/// しきい値超過時に全データページを flush して WAL を truncate する。
/// </summary>
public sealed class WalBloatRegressionTests : IDisposable
{
    private readonly string _dir;

    public WalBloatRegressionTests()
        => _dir = Path.Combine(Path.GetTempPath(), "quiver_walbloat_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static long DirSize(string path)
        => !Directory.Exists(path)
            ? 0
            : new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    /// <summary>
    /// filterchain ベンチ GlobalSetup を縮小したグラフ構築 — ノードを 1 TX、
    /// エッジを <paramref name="batchSize"/> 本ごとにバッチ commit する。生成したエッジ数を返す。
    /// </summary>
    private long BuildGraph(GraphDatabaseOptions? options, int nodeCount, int avgDegree, int batchSize)
    {
        var rnd = new Random(42);
        var nodeIds = new NodeId[nodeCount];
        using var db = GraphDatabase.Open(_dir, options);

        using (var tx = db.BeginTransaction())
        {
            for (int i = 0; i < nodeCount; i++)
                nodeIds[i] = tx.CreateNode("Person");
            tx.Commit();
        }

        long created = 0;
        var batchTx = db.BeginTransaction();
        try
        {
            for (int i = 0; i < nodeCount; i++)
            {
                for (int d = 0; d < avgDegree; d++)
                {
                    batchTx.CreateRelationship(nodeIds[i], nodeIds[rnd.Next(nodeCount)], "KNOWS");
                    if (++created % batchSize == 0)
                    {
                        batchTx.Commit();
                        batchTx.Dispose();
                        batchTx = db.BeginTransaction();
                    }
                }
            }
            batchTx.Commit();
        }
        finally
        {
            batchTx.Dispose();
        }
        return created;
    }

    [Fact]
    public void Wal_stays_bounded_while_building_graph_with_default_options()
    {
        const int nodeCount = 6_000;
        const int avgDegree = 8;
        long edges = BuildGraph(options: null, nodeCount, avgDegree, batchSize: 10_000);

        long walBytes = DirSize(Path.Combine(_dir, "wal"));

        // 旧挙動 (毎エッジ 6 ページ × 8KB のフルページログ、truncate 無し) なら
        // edges × ~48KB に達する。案C のコアレスでこれが桁違いに縮む。
        long oldBehaviorEstimate = edges * 6 * 8192;
        walBytes.Should().BeLessThan(oldBehaviorEstimate / 8,
            "案C のコアレスで WAL は旧フルページログより桁違いに小さいはず");
        walBytes.Should().BeLessThan(256L * 1024 * 1024,
            "WAL が数百 MB を超えて肥大してはならない");

        // データが壊れていないこと (clean close → reopen)。
        CountKnowsEdges().Should().Be(edges);
    }

    [Fact]
    public void Checkpoint_truncates_old_wal_segments_and_data_survives_reopen()
    {
        // 小さい WAL セグメント + 小さい checkpoint しきい値で truncate を確実に発火させる。
        var options = new GraphDatabaseOptions
        {
            WalSegmentSize = 256 * 1024,
            CheckpointThresholdBytes = 256 * 1024,
        };
        const int nodeCount = 5_000;
        const int avgDegree = 8;
        long edges = BuildGraph(options, nodeCount, avgDegree, batchSize: 8_000);

        string walDir = Path.Combine(_dir, "wal");
        var segments = Directory.GetFiles(walDir, "wal.????????.log");

        // truncate が効いていれば、構築中に多数のセグメント (40k エッジ ≈ 数十本) が作られても
        // 残るのは末尾の数本だけ。最古セグメント wal.00000000.log は削除されているはず。
        segments.Length.Should().BeLessThanOrEqualTo(4,
            "checkpoint で過去 WAL セグメントが truncate されているはず");
        File.Exists(Path.Combine(walDir, "wal.00000000.log")).Should().BeFalse(
            "最古 WAL セグメントは truncate 済みのはず");
        DirSize(walDir).Should().BeLessThan(4L * 1024 * 1024,
            "truncate 後の WAL は数 MB 程度に収まるはず");

        // truncate 後も reopen でデータが完全に復元できること。
        CountKnowsEdges(options).Should().Be(edges);
    }

    /// <summary>
    /// DB を reopen し、Person からの KNOWS 展開数 = 全 KNOWS エッジ数を数える。
    /// 構築時の各エッジは source → target の KNOWS なので、全 Person の Out("KNOWS") 合計が
    /// エッジ総数に一致する。reopen 時の WAL リカバリが正しく働いたことの検証になる。
    /// </summary>
    private long CountKnowsEdges(GraphDatabaseOptions? options = null)
    {
        using var db = GraphDatabase.Open(_dir, options);
        using var tx = db.BeginReadOnlyTransaction();
        var g = tx.G(db.Schema);
        return g.Nodes().HasLabel("Person").Out("KNOWS").ToList().Count;
    }
}
