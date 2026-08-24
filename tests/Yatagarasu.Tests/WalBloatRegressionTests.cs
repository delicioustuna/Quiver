using FluentAssertions;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Xunit;

namespace Yatagarasu.Tests;

/// <summary>
/// checkpoint + truncate 配線とトランザクション内 PageImage coalescing の回帰センチネル。
///
/// FilterChainExpandBenchmarks の GlobalSetup で WAL が単発 160 GB に肥大した不具合の再発防止。
/// 根本原因は (1) UnpinDirty が毎回フルページ 8KB を WAL に追記し、(2) checkpoint/truncate が
/// production から一度も呼ばれず、(3) FlushMeta() がエッジ作成ごとに同一ヘッダページを再ログ
/// していたこと。1 トランザクション内の同一ページを最新版 1 件に畳み込み、
/// しきい値超過時に全データページを flush して WAL を truncate する。
/// </summary>
public sealed class WalBloatRegressionTests : IDisposable
{
    private readonly string _dir;

    public WalBloatRegressionTests()
        => _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_walbloat_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // WAL は単一のサイドカーファイル graph.yata-wal であり、正常終了時に削除されるため、
    // 「構築中に肥大しないこと」は DB を開いている間の peak サイズで測る。
    private string WalPath => Path.Combine(_dir, "graph.yata-wal");
    private long _peakWalBytes;

    private void SampleWal()
    {
        if (File.Exists(WalPath))
            _peakWalBytes = Math.Max(_peakWalBytes, new FileInfo(WalPath).Length);
    }

    /// <summary>
    /// filterchain ベンチ GlobalSetup を縮小したグラフ構築 — Vertexを 1 TX、
    /// エッジを <paramref name="batchSize"/> 本ごとにバッチ commit する。生成したエッジ数を返す。
    /// 各バッチ commit 後に WAL サイドカーの peak サイズを <see cref="_peakWalBytes"/> へ記録する。
    /// </summary>
    private long BuildGraph(YatagarasuDatabaseOptions? options, int vertexCount, int avgDegree, int batchSize)
    {
        var rnd = new Random(42);
        var vertexIds = new VertexId[vertexCount];
        _peakWalBytes = 0;
        using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata"), options);

        using (var tx = db.BeginWriteTransaction())
        {
            for (int i = 0; i < vertexCount; i++)
                vertexIds[i] = tx.CreateVertex("Person");
            tx.Commit();
        }
        SampleWal();

        long created = 0;
        var batchTx = db.BeginWriteTransaction();
        try
        {
            for (int i = 0; i < vertexCount; i++)
            {
                for (int d = 0; d < avgDegree; d++)
                {
                    batchTx.CreateEdge(vertexIds[i], vertexIds[rnd.Next(vertexCount)], "KNOWS");
                    if (++created % batchSize == 0)
                    {
                        SampleWal(); // checkpoint compaction 直前のサイズも測る
                        batchTx.Commit();
                        batchTx.Dispose();
                        SampleWal();
                        batchTx = db.BeginWriteTransaction();
                    }
                }
            }
            batchTx.Commit();
        }
        finally
        {
            batchTx.Dispose();
        }
        SampleWal();
        return created;
    }

    [Fact]
    public void Wal_stays_bounded_while_building_graph_with_default_options()
    {
        const int vertexCount = 6_000;
        const int avgDegree = 8;
        long edges = BuildGraph(options: null, vertexCount, avgDegree, batchSize: 10_000);

        long walBytes = _peakWalBytes;

        // 旧挙動 (毎エッジ 6 ページ × 8KB のフルページログ、truncate 無し) なら
        // edges × ~48KB に達する。PageImage coalescing と checkpoint compaction でこれが桁違いに縮む。
        long oldBehaviorEstimate = edges * 6 * 8192;
        walBytes.Should().BeLessThan(oldBehaviorEstimate / 8,
            "PageImage coalescingでWALのpeakは旧フルページログより桁違いに小さいはず");
        walBytes.Should().BeLessThan(256L * 1024 * 1024,
            "WAL が数百 MB を超えて肥大してはならない");

        // 正常終了時に WAL サイドカーは削除され、静止時には graph.yata だけが残る。
        File.Exists(WalPath).Should().BeFalse(
            "クリーン終了後は WAL サイドカーが削除されているはず");

        // データが壊れていないこと (clean close → reopen)。
        CountKnowsEdges().Should().Be(edges);
    }

    [Fact]
    public void Checkpoint_compacts_single_wal_file_and_data_survives_reopen()
    {
        // 小さい checkpoint しきい値でコンパクションを確実に発火させる。
        var options = new YatagarasuDatabaseOptions
        {
            CheckpointThresholdBytes = 256 * 1024,
        };
        const int vertexCount = 5_000;
        const int avgDegree = 8;
        long edges = BuildGraph(options, vertexCount, avgDegree, batchSize: 8_000);

        // 単一ファイル WAL はチェックポイント時の圧縮で先頭の不要部分を詰め、
        // 構築中の peak でも数 MB に収まるはず (旧挙動なら 40k エッジで数十 MB+)。
        _peakWalBytes.Should().BeLessThan(8L * 1024 * 1024,
            "checkpoint のコンパクションで WAL の peak は数 MB に収まるはず");

        // クリーン終了で WAL サイドカーは削除され静止時は graph.yata のみ。
        File.Exists(WalPath).Should().BeFalse(
            "クリーン終了後は WAL サイドカーが削除されているはず");

        // コンパクション後も reopen でデータが完全に復元できること。
        CountKnowsEdges(options).Should().Be(edges);
    }

    /// <summary>
    /// DB を reopen し、Person からの KNOWS 展開数 = 全 KNOWS エッジ数を数える。
    /// 構築時の各エッジは source → target の KNOWS なので、全 Person の Out("KNOWS") 合計が
    /// エッジ総数に一致する。reopen 時の WAL リカバリが正しく働いたことの検証になる。
    /// </summary>
    private long CountKnowsEdges(YatagarasuDatabaseOptions? options = null)
    {
        using var db = YatagarasuDatabase.Open(System.IO.Path.Combine(_dir, "graph.yata"), options);
        using var tx = db.BeginReadTransaction();
        var g = tx.Query;
        return g.Vertices().HasLabel("Person").Out("KNOWS").ToList().Count;
    }
}
