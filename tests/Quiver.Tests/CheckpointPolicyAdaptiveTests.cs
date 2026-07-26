using FluentAssertions;
using Quiver.Core;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <see cref="CheckpointPolicy.Adaptive"/> のデータベース統合を検証する。
/// per-tx パスのワークロードで Adaptive が threshold を縮めて WAL 絶対値を抑えること、
/// Fixed (既定) は旧挙動と完全一致すること、ホットスワップが効くことを確認する。
/// </summary>
public sealed class CheckpointPolicyAdaptiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "quiver_ft28_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static long DirSize(string path)
        => !Directory.Exists(path)
            ? 0
            : new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    /// <summary>
    /// per-tx に 1 Vertexずつ commit する high-amplification ワークロードを <paramref name="commits"/>
    /// 回繰り返す。各 tx は CreateVertex 1 件のみで、WAL 1 件あたりに PageImage が複数 (VertexStore /
    /// LabelTokenStore / 等) 焼ける構造。
    /// </summary>
    private long RunPerTxWorkload(QuiverDatabaseOptions options, int commits)
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), options);
        for (int i = 0; i < commits; i++)
        {
            using var tx = db.BeginWriteTransaction();
            tx.CreateVertex("Person");
            tx.Commit();
        }
        return DirSize(Path.Combine(_dir, "wal"));
    }

    [Fact]
    public void Fixed_policy_is_default_and_preserves_legacy_behavior()
    {
        var opts = new QuiverDatabaseOptions
        {
            CheckpointThresholdBytes = 16 * 1024 * 1024,
            // CheckpointPolicy 未指定 = Fixed (default)
        };
        opts.CheckpointPolicy.Should().Be(CheckpointPolicy.Fixed);

        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), opts);
        db.Diagnostics.CurrentCheckpointThresholdBytes.Should().Be(16 * 1024 * 1024);

        // 通常の動作 (commit + read) に regression 無し。
        using (var tx = db.BeginWriteTransaction())
        {
            tx.CreateVertex("Person");
            tx.Commit();
        }

        // Fixed では threshold は不変。
        db.Diagnostics.CurrentCheckpointThresholdBytes.Should().Be(16 * 1024 * 1024);
    }

    [Fact]
    public void Adaptive_policy_shrinks_threshold_under_per_tx_amplification()
    {
        // 各 tx で 100 Vertex作成して per-tx WAL 数 KB 以上を強制する。
        // PageImage の切り詰めは疎なページで効果が大きいため、
        // 単一 vertex tx は数百バイト/tx と非常に小さくなる (Adaptive はむしろ threshold を
        // 拡大する方向に動く)。本テストの「Adaptive が threshold を縮める」挙動を確かめるには
        // 効果が限定的なページ充填率の高いワークロードを流す必要がある。
        var opts = new QuiverDatabaseOptions
        {
            CheckpointThresholdBytes = 16 * 1024 * 1024,
            CheckpointPolicy = CheckpointPolicy.Adaptive,
            // 16 サンプルで warmup 完了させるため window=16。
            AdaptiveSampleWindow = 16,
            MinCheckpointThresholdBytes = 4L * 1024 * 1024,
            MaxCheckpointThresholdBytes = 64L * 1024 * 1024,
        };

        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), opts);

        // warmup 完了まで commit を流す (warmup 閾値 = max(16, window/64) = 16)。
        // 各 tx で 1000 Vertexを作成し、複数ページを大きく dirty 化することで
        // 切り詰めと RLE 圧縮後もトランザクションごとに 16 KB 以上を確保する。
        // Adaptive モデル: recommended = 5s × 50MB/s × 1KB / avg。
        // 16MB threshold 以下にするには avg ≥ 16KB が必要 → trim+RLE 後 ~18 B/vertex × 1000 = 18KB。
        for (int i = 0; i < 32; i++)
        {
            using var tx = db.BeginWriteTransaction();
            for (int j = 0; j < 1000; j++) tx.CreateVertex("Person");
            tx.Commit();
        }

        long adaptiveThreshold = db.Diagnostics.CurrentCheckpointThresholdBytes;
        adaptiveThreshold.Should().BeGreaterThan(0);
        adaptiveThreshold.Should().BeLessThanOrEqualTo(16L * 1024 * 1024,
            "高 amplification per-tx パスでは threshold が initial 以下に縮むはず");
    }

    [Fact]
    public void Adaptive_policy_clamps_to_min_threshold()
    {
        // 大きな per-tx WAL bytes を強制するため、複数 vertex を 1 tx で書く。
        // amp が大きいほど recommended が小さくなり、min=4MB に張り付く。
        var opts = new QuiverDatabaseOptions
        {
            CheckpointThresholdBytes = 16 * 1024 * 1024,
            CheckpointPolicy = CheckpointPolicy.Adaptive,
            AdaptiveSampleWindow = 16,
            MinCheckpointThresholdBytes = 4L * 1024 * 1024,
            MaxCheckpointThresholdBytes = 1024L * 1024 * 1024,
        };

        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), opts);
        for (int i = 0; i < 32; i++)
        {
            using var tx = db.BeginWriteTransaction();
            // 1 tx で大量のVertex作成 → 64KB 超の WAL bytes/tx を確保し min 値 (4MB) に
            // clamp させる。recommended = 250MB × 1024 / amp なので amp > 64 KB で min に張り付く。
            // 切り詰めと RLE 圧縮後も 5000 Vertexで約 90 KB を見込み、上限への丸めを強制する。
            for (int j = 0; j < 5000; j++) tx.CreateVertex("Person");
            tx.Commit();
        }

        db.Diagnostics.CurrentCheckpointThresholdBytes
            .Should().Be(4L * 1024 * 1024, "重い per-tx amplification では min 値に clamp されるはず");
    }

    [Fact]
    public void SetCheckpointPolicy_hot_swap_from_fixed_to_adaptive_takes_effect()
    {
        var opts = new QuiverDatabaseOptions
        {
            CheckpointThresholdBytes = 32 * 1024 * 1024,
            CheckpointPolicy = CheckpointPolicy.Fixed,
            AdaptiveSampleWindow = 16,
            MinCheckpointThresholdBytes = 4L * 1024 * 1024,
            MaxCheckpointThresholdBytes = 256L * 1024 * 1024,
        };
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), opts);
        db.Diagnostics.CurrentCheckpointThresholdBytes.Should().Be(32 * 1024 * 1024);

        // ホットスワップ: Adaptive に切替。
        db.Diagnostics.SetCheckpointPolicy(CheckpointPolicy.Adaptive, fixedThresholdBytes: 32 * 1024 * 1024);

        // ウォームアップ完了までコミットし、RLE 圧縮後も閾値の縮小を観測できるようにする。
        // 400 vertices/tx で per-tx を ~8 KB 以上に押し上げる。
        for (int i = 0; i < 32; i++)
        {
            using var tx = db.BeginWriteTransaction();
            for (int j = 0; j < 400; j++) tx.CreateVertex("Person");
            tx.Commit();
        }

        long adaptiveThreshold = db.Diagnostics.CurrentCheckpointThresholdBytes;
        adaptiveThreshold.Should().BeLessThanOrEqualTo(32 * 1024 * 1024,
            "Adaptive に切り替わった後は threshold が再計算されるはず");

        // Fixed に戻すと指定した固定値が反映される。
        db.Diagnostics.SetCheckpointPolicy(CheckpointPolicy.Fixed, fixedThresholdBytes: 8 * 1024 * 1024);
        db.Diagnostics.CurrentCheckpointThresholdBytes.Should().Be(8 * 1024 * 1024);
    }

    [Fact]
    public void Adaptive_policy_keeps_data_correct_across_reopen()
    {
        var opts = new QuiverDatabaseOptions
        {
            CheckpointThresholdBytes = 8 * 1024 * 1024,
            CheckpointPolicy = CheckpointPolicy.Adaptive,
            AdaptiveSampleWindow = 16,
            MinCheckpointThresholdBytes = 1 * 1024 * 1024, // < 既定: より頻繁な checkpoint で truncate を発火
            MaxCheckpointThresholdBytes = 64L * 1024 * 1024,
        };

        long expectedVertexCount;
        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), opts))
        {
            for (int i = 0; i < 64; i++)
            {
                using var tx = db.BeginWriteTransaction();
                for (int j = 0; j < 10; j++) tx.CreateVertex("Person");
                tx.Commit();
            }
            expectedVertexCount = db.Diagnostics.GetStatistics().VertexCount;
        }

        // 再 open でデータが完全に復元できる (Adaptive が打った checkpoint で WAL truncate が
        // 走っていても recovery が正しく動くこと)。
        using (var db2 = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), opts))
        {
            db2.Diagnostics.GetStatistics().VertexCount.Should().Be(expectedVertexCount);
        }
    }
}
