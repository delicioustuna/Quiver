using FluentAssertions;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// オプトイン列 DDL の並行性契約と、列データのクラッシュリカバリーを検証する。
/// DDL はアクティブなトランザクションが無いことを要求する。
/// <c>CreateSnapshot</c> の出力を WAL redo で開き、列の先頭ページの再実行も通す。
/// </summary>
public sealed class ColumnHardeningTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ColumnHardeningTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_colhard_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void CreateColumn_requires_no_active_transaction()
    {
        using var db = QuiverDatabase.Open(_path);
        using var tx = db.BeginWriteTransaction(); // アクティブ tx を保持したまま
        Action act = () => db.CreateColumn(EntityKind.Vertex, "x");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DropColumn_requires_no_active_transaction()
    {
        using var db = QuiverDatabase.Open(_path);
        db.CreateColumn(EntityKind.Vertex, "x").Should().BeTrue();
        using var tx = db.BeginReadTransaction(); // reader でも DDL は不可
        Action act = () => db.DropColumn(EntityKind.Vertex, "x");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Column_data_survives_reopen_and_aggregates()
    {
        using (var db = QuiverDatabase.Open(_path))
        {
            using (var tx = db.BeginWriteTransaction())
            {
                var a = tx.CreateVertex("A");
                var b = tx.CreateVertex("B");
                for (int i = 0; i < 4; i++)
                {
                    var r = tx.CreateEdge(a, b, "R");
                    tx.SetProperty(r, "w", PropertyValue.FromInt64(10));
                }
                tx.Commit();
            }
            db.CreateColumn(EntityKind.Edge, "w").Should().BeTrue();
            using (var tx = db.BeginWriteTransaction())
            {
                foreach (var r in AllEdges(db)) tx.SetProperty(r, "w", PropertyValue.FromInt64(15));
                tx.Commit();
            }
        }

        // 再オープン (clean close → recovery)。列の head ページ + catalog が永続している。
        using (var db2 = QuiverDatabase.Open(_path))
        {
            db2.CreateColumn(EntityKind.Edge, "w").Should().BeFalse(); // 登録永続
            using var tx = db2.BeginReadTransaction();
            tx.Query.Edges().ToList().Should().HaveCount(4);
            // 列スキャン集約は committed 最新値 (15 × 4 = 60)。
            tx.Query.Edges().SumLong("w").Should().Be(60);
        }
    }

    [Fact]
    public void Column_data_survives_snapshot_recovery()
    {
        // CreateColumn の構築を WAL 文脈下で commit するため、online backup (CreateSnapshot) の
        // ターゲットも WAL redo で列 head ページ + catalog + 列テナント page-table を復元できる。
        var snapPath = Path.Combine(_dir, "snap.quiver");
        using (var db = QuiverDatabase.Open(_path))
        {
            using (var tx = db.BeginWriteTransaction())
            {
                var a = tx.CreateVertex("A");
                var b = tx.CreateVertex("B");
                for (int i = 0; i < 4; i++)
                {
                    var r = tx.CreateEdge(a, b, "R");
                    tx.SetProperty(r, "w", PropertyValue.FromInt64(10));
                }
                tx.Commit();
            }
            db.CreateColumn(EntityKind.Edge, "w").Should().BeTrue();
            db.CreateSnapshot(snapPath);
        }

        using (var snap = QuiverDatabase.Open(snapPath))
        {
            snap.CreateColumn(EntityKind.Edge, "w").Should().BeFalse(); // 登録 redo 済
            using var tx = snap.BeginReadTransaction();
            tx.Query.Edges().ToList().Should().HaveCount(4);
            tx.Query.Edges().SumLong("w").Should().Be(40); // 10 × 4
        }
    }

    private static List<EdgeId> AllEdges(QuiverDatabase db)
    {
        using var tx = db.BeginReadTransaction();
        return tx.Query.Edges().ToList();
    }
}
