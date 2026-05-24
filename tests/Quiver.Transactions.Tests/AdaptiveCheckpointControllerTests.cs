using FluentAssertions;
using Xunit;

namespace Quiver.Transactions.Tests;

public class AdaptiveCheckpointControllerTests
{
    private static AdaptiveCheckpointController Create(
        long initialThreshold = 64L * 1024 * 1024,
        TimeSpan? target = null,
        long min = 4L * 1024 * 1024,
        long max = 1024L * 1024 * 1024,
        int window = 1000)
        => new(initialThreshold, target ?? TimeSpan.FromSeconds(5), min, max, window);

    [Fact]
    public void Initial_threshold_is_clamped_to_range()
    {
        var c = Create(initialThreshold: 1024); // below min
        c.CurrentThresholdBytes.Should().Be(4L * 1024 * 1024);

        var c2 = Create(initialThreshold: 10L * 1024 * 1024 * 1024); // above max
        c2.CurrentThresholdBytes.Should().Be(1024L * 1024 * 1024);
    }

    [Fact]
    public void Warmup_period_returns_initial_threshold_unchanged()
    {
        var c = Create(initialThreshold: 64L * 1024 * 1024);
        c.HasEnoughSamples.Should().BeFalse();
        c.CurrentThresholdBytes.Should().Be(64L * 1024 * 1024);

        // 数件だけ食わせても warmup 完了しない (>= max(16, window/64))
        for (int i = 0; i < 8; i++) c.RecordTxBytes(1024);
        c.HasEnoughSamples.Should().BeFalse();
    }

    [Fact]
    public void Heavy_per_tx_amplification_shrinks_threshold()
    {
        // window=1000, baseline=1KB, target_bytes_bound = 5s * 50MB/s = 250MB。
        // 平均 1MB/tx を食わせると recommended = 250MB * 1024 / 1MB = 256000 bytes → min=4MB に clamp。
        var c = Create(window: 64); // small window for fast warmup
        for (int i = 0; i < 64; i++) c.RecordTxBytes(1024 * 1024); // 1 MB / tx
        c.HasEnoughSamples.Should().BeTrue();
        c.CurrentThresholdBytes.Should().Be(4L * 1024 * 1024);
    }

    [Fact]
    public void Tiny_per_tx_amplification_raises_threshold_toward_max()
    {
        // 平均 64 B/tx (= bulk-like tiny commit) → recommended = 250MB * 1024 / 64 = ~4 GB → max=1GB clamp
        var c = Create(window: 64);
        for (int i = 0; i < 64; i++) c.RecordTxBytes(200); // small but above MinSampleBytes=128
        c.HasEnoughSamples.Should().BeTrue();
        c.CurrentThresholdBytes.Should().Be(1024L * 1024 * 1024);
    }

    [Fact]
    public void Mid_range_per_tx_amplification_lands_between_min_and_max()
    {
        // 平均 ~50 KB/tx → recommended ≈ 250MB * 1024 / 50000 ≈ 5.1 MB
        var c = Create(window: 64);
        for (int i = 0; i < 64; i++) c.RecordTxBytes(50 * 1024);
        c.HasEnoughSamples.Should().BeTrue();
        long t = c.CurrentThresholdBytes;
        t.Should().BeGreaterThan(4L * 1024 * 1024);
        t.Should().BeLessThan(20L * 1024 * 1024);
    }

    [Fact]
    public void Readonly_tx_samples_are_excluded()
    {
        // 100 件の readonly (50B) を食わせても warmup 完了せず、threshold は initial のまま。
        var c = Create(window: 64);
        for (int i = 0; i < 100; i++) c.RecordTxBytes(50);
        c.HasEnoughSamples.Should().BeFalse();
        c.SampleCount.Should().Be(0);
    }

    [Fact]
    public void Sliding_window_drops_old_samples()
    {
        // window=4 で warmup 閾値も極小 (max(16, 0) = 16 だが SampleCount <= 4 なので false)。
        // ここでは window=128 にして 16 件で warmup 完了させ、その後 重い tx で平均を上げる。
        var c = Create(window: 128);
        // 最初 16 件: 200 KB/tx (recommended ~1.25 MB → min=4MB clamp)
        for (int i = 0; i < 16; i++) c.RecordTxBytes(200 * 1024);
        c.HasEnoughSamples.Should().BeTrue();
        long earlyThreshold = c.CurrentThresholdBytes;

        // さらに 200 件の 1KB/tx を入れる。最初の重い 16 件は window から押し出される
        // (window=128 なので最後の 128 サンプル = すべて 1KB)。recommended ≈ 250MB → max 1GB。
        for (int i = 0; i < 200; i++) c.RecordTxBytes(1024);
        long lateThreshold = c.CurrentThresholdBytes;

        // 重いワークロードの threshold (4MB clamp) < 軽いワークロードの threshold (250MB)
        lateThreshold.Should().BeGreaterThan(earlyThreshold);
    }

    [Fact]
    public void Min_max_validation_rejects_invalid_ctor_args()
    {
        Action a1 = () => new AdaptiveCheckpointController(
            64 * 1024 * 1024, TimeSpan.FromSeconds(5), minThresholdBytes: 0, maxThresholdBytes: 1024, sampleWindow: 100);
        a1.Should().Throw<ArgumentOutOfRangeException>();

        Action a2 = () => new AdaptiveCheckpointController(
            64 * 1024 * 1024, TimeSpan.FromSeconds(5), minThresholdBytes: 2048, maxThresholdBytes: 1024, sampleWindow: 100);
        a2.Should().Throw<ArgumentOutOfRangeException>();

        Action a3 = () => new AdaptiveCheckpointController(
            64 * 1024 * 1024, TimeSpan.Zero, minThresholdBytes: 1024, maxThresholdBytes: 2048, sampleWindow: 100);
        a3.Should().Throw<ArgumentOutOfRangeException>();

        Action a4 = () => new AdaptiveCheckpointController(
            64 * 1024 * 1024, TimeSpan.FromSeconds(5), minThresholdBytes: 1024, maxThresholdBytes: 2048, sampleWindow: 0);
        a4.Should().Throw<ArgumentOutOfRangeException>();
    }
}
