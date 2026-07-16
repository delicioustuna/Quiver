using FluentAssertions;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// Vacuum が列の余剰差分版、すなわち上書きで退避された旧版をマージすることを検証する。
/// 上書きで差分を蓄積し、可視性ホライズンより古いコミット済み版が回収されても
/// 集約結果が変わらないことを確認する。
/// </summary>
public sealed class ColumnCompactionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ColumnCompactionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_colcompact_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Vacuum_merges_superseded_column_delta_versions()
    {
        using var db = QuiverDatabase.Open(_path);
        // 複数 edge を使う (heap version-chain guard は ~entity 数依存なので、単一 edge を多重上書き
        // すると小グラフで guard に当たる — それは列とは無関係の既存ヒューリスティック)。
        var edges = new EdgeId[6];
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateVertex("A");
            var b = tx.CreateVertex("B");
            for (int i = 0; i < edges.Length; i++)
            {
                edges[i] = tx.CreateEdge(a, b, "R");
                tx.SetProperty(edges[i], "w", PropertyValue.FromInt64(1));
            }
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Edge, "w").Should().BeTrue();

        // 各 edge を数回上書きして delta (超過版) を積む。各 commit が旧 head を delta へ退避する。
        for (long round = 2; round <= 4; round++)
        {
            using var tx = db.BeginTransaction();
            foreach (var r in edges)
                tx.SetProperty(r, "w", PropertyValue.FromInt64(round));
            tx.Commit();
        }

        // 最新値が見える (全 edge が w=4 → 合計 24)。
        using (var tx = db.BeginReadOnlyTransaction())
            tx.G(db.Schema).Edges().SumLong("w").Should().Be(24);

        // vacuum で horizon 未満の commit 済み超過版を回収する。
        var report = db.Vacuum();
        report.Skipped.Should().BeFalse();
        report.ReclaimedColumnVersions.Should().BeGreaterThan(0);

        // 回収後も最新値は不変。
        using (var tx = db.BeginReadOnlyTransaction())
            tx.G(db.Schema).Edges().SumLong("w").Should().Be(24);

        // 二度目の vacuum では回収するものが無い (冪等)。
        db.Vacuum().ReclaimedColumnVersions.Should().Be(0);
    }

    [Fact]
    public void Vacuum_without_columns_reports_zero_column_reclaim()
    {
        using var db = QuiverDatabase.Open(_path);
        using (var tx = db.BeginTransaction())
        {
            var n = tx.CreateVertex("A");
            tx.SetProperty(n, "x", PropertyValue.FromInt64(1));
            tx.Commit();
        }
        db.Vacuum().ReclaimedColumnVersions.Should().Be(0);
    }
}
