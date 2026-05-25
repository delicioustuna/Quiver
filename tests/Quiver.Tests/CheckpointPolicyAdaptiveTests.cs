using FluentAssertions;
using Quiver.Core;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// FT-28: <see cref="CheckpointPolicy.Adaptive"/> の DB 統合テスト。
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
    /// per-tx に 1 ノードずつ commit する high-amplification ワークロードを <paramref name="commits"/>
    /// 回繰り返す。各 tx は CreateNode 1 件のみで、WAL 1 件あたりに PageImage が複数 (NodeStore /
    /// LabelTokenStore / 等) 焼ける構造。
    /// </summary>
    private long RunPerTxWorkload(GraphDatabaseOptions options, int commits)
    {
        using var db = GraphDatabase.Open(_dir, options);
        for (int i = 0; i < commits; i++)
        {
            using var tx = db.BeginTransaction();
            tx.CreateNode("Person");
            tx.Commit();
        }
        return DirSize(Path.Combine(_dir, "wal"));
    }

    [Fact]
    public void Fixed_policy_is_default_and_preserves_legacy_behavior()
    {
        var opts = new GraphDatabaseOptions
        {
            WalSegmentSize = 256 * 1024,
            CheckpointThresholdBytes = 16 * 1024 * 1024,
            // CheckpointPolicy 未指定 = Fixed (default)
        };
        opts.CheckpointPolicy.Should().Be(CheckpointPolicy.Fixed);

        using var db = GraphDatabase.Open(_dir, opts);
        db.Diagnostics.CurrentCheckpointThresholdBytes.Should().Be(16 * 1024 * 1024);

        // 通常の動作 (commit + read) に regression 無し。
        using (var tx = db.BeginTransaction())
        {
            tx.CreateNode("Person");
            tx.Commit();
        }

        // Fixed では threshold は不変。
        db.Diagnostics.CurrentCheckpointThresholdBytes.Should().Be(16 * 1024 * 1024);
    }

    [Fact]
    public void Adaptive_policy_shrinks_threshold_under_per_tx_amplification()
    {
        // 各 tx で 100 ノード作成して per-tx WAL 数 KB 以上を強制する。
        // FT-29 (PageImage trim) 後は sparse page では trim が効くため、
        // 単一 node tx は数百バイト/tx と非常に小さくなる (Adaptive はむしろ threshold を
        // 拡大する方向に動く)。本テストの「Adaptive が threshold を縮める」挙動を確かめるには
        // FT-29 でも trim 効果が限定的になる「ページ充填率の高い workload」を流す必要がある。
        var opts = new GraphDatabaseOptions
        {
            WalSegmentSize = 256 * 1024,
            CheckpointThresholdBytes = 16 * 1024 * 1024,
            CheckpointPolicy = CheckpointPolicy.Adaptive,
            // 16 サンプルで warmup 完了させるため window=16。
            AdaptiveSampleWindow = 16,
            MinCheckpointThresholdBytes = 4L * 1024 * 1024,
            MaxCheckpointThresholdBytes = 64L * 1024 * 1024,
        };

        using var db = GraphDatabase.Open(_dir, opts);

        // warmup 完了まで commit を流す (warmup 閾値 = max(16, window/64) = 16)。
        // 各 tx で 300 ノードを作成し、複数ページを大きく dirty 化することで FT-29 trim 効果を抑え、
        // per-tx で十分な WAL バイト数 (~16KB 以上) を発生させる。
        // Adaptive モデル: recommended = 5s × 50MB/s × 1KB / avg。
        // 16MB threshold 以下にするには avg ≥ 16KB が必要 → 約 300 ノード/tx。
        for (int i = 0; i < 32; i++)
        {
            using var tx = db.BeginTransaction();
            for (int j = 0; j < 300; j++) tx.CreateNode("Person");
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
        // 大きな per-tx WAL bytes を強制するため、複数 node を 1 tx で書く。
        // amp が大きいほど recommended が小さくなり、min=4MB に張り付く。
        var opts = new GraphDatabaseOptions
        {
            WalSegmentSize = 256 * 1024,
            CheckpointThresholdBytes = 16 * 1024 * 1024,
            CheckpointPolicy = CheckpointPolicy.Adaptive,
            AdaptiveSampleWindow = 16,
            MinCheckpointThresholdBytes = 4L * 1024 * 1024,
            MaxCheckpointThresholdBytes = 1024L * 1024 * 1024,
        };

        using var db = GraphDatabase.Open(_dir, opts);
        for (int i = 0; i < 32; i++)
        {
            using var tx = db.BeginTransaction();
            // 1 tx で大量のノード作成 → 100KB 超の WAL bytes/tx を確保し min 値 (4MB) に
            // clamp させる。recommended = 250MB × 1024 / amp なので amp > 64 KB で min に張り付く。
            for (int j = 0; j < 2000; j++) tx.CreateNode("Person");
            tx.Commit();
        }

        db.Diagnostics.CurrentCheckpointThresholdBytes
            .Should().Be(4L * 1024 * 1024, "重い per-tx amplification では min 値に clamp されるはず");
    }

    [Fact]
    public void SetCheckpointPolicy_hot_swap_from_fixed_to_adaptive_takes_effect()
    {
        var opts = new GraphDatabaseOptions
        {
            WalSegmentSize = 256 * 1024,
            CheckpointThresholdBytes = 32 * 1024 * 1024,
            CheckpointPolicy = CheckpointPolicy.Fixed,
            AdaptiveSampleWindow = 16,
            MinCheckpointThresholdBytes = 4L * 1024 * 1024,
            MaxCheckpointThresholdBytes = 256L * 1024 * 1024,
        };
        using var db = GraphDatabase.Open(_dir, opts);
        db.Diagnostics.CurrentCheckpointThresholdBytes.Should().Be(32 * 1024 * 1024);

        // ホットスワップ: Adaptive に切替。
        db.Diagnostics.SetCheckpointPolicy(CheckpointPolicy.Adaptive, fixedThresholdBytes: 32 * 1024 * 1024);

        // warmup 完了まで commit を流す。
        for (int i = 0; i < 32; i++)
        {
            using var tx = db.BeginTransaction();
            for (int j = 0; j < 20; j++) tx.CreateNode("Person");
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
        var opts = new GraphDatabaseOptions
        {
            WalSegmentSize = 256 * 1024,
            CheckpointThresholdBytes = 8 * 1024 * 1024,
            CheckpointPolicy = CheckpointPolicy.Adaptive,
            AdaptiveSampleWindow = 16,
            MinCheckpointThresholdBytes = 1 * 1024 * 1024, // < 既定: より頻繁な checkpoint で truncate を発火
            MaxCheckpointThresholdBytes = 64L * 1024 * 1024,
        };

        long expectedNodeCount;
        using (var db = GraphDatabase.Open(_dir, opts))
        {
            for (int i = 0; i < 64; i++)
            {
                using var tx = db.BeginTransaction();
                for (int j = 0; j < 10; j++) tx.CreateNode("Person");
                tx.Commit();
            }
            expectedNodeCount = db.Diagnostics.GetStatistics().NodeCount;
        }

        // 再 open でデータが完全に復元できる (Adaptive が打った checkpoint で WAL truncate が
        // 走っていても recovery が正しく動くこと)。
        using (var db2 = GraphDatabase.Open(_dir, opts))
        {
            db2.Diagnostics.GetStatistics().NodeCount.Should().Be(expectedNodeCount);
        }
    }
}
