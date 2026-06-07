using FluentAssertions;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// ARCH-5c Phase 5 (5g): hardening。opt-in 列 DDL の並行性契約 (アクティブ tx 無しを要求) と、
/// 列データの crash recovery (CreateSnapshot のターゲットは WAL redo で開くため、列 head ページの
/// redo を実地に通す) を検証する。
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
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction(); // アクティブ tx を保持したまま
        Action act = () => db.CreateColumn(EntityKind.Node, "x");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DropColumn_requires_no_active_transaction()
    {
        using var db = GraphDatabase.Open(_path);
        db.CreateColumn(EntityKind.Node, "x").Should().BeTrue();
        using var tx = db.BeginReadOnlyTransaction(); // reader でも DDL は不可
        Action act = () => db.DropColumn(EntityKind.Node, "x");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Column_data_survives_reopen_and_aggregates()
    {
        using (var db = GraphDatabase.Open(_path))
        {
            using (var tx = db.BeginTransaction())
            {
                var a = tx.CreateNode("A");
                var b = tx.CreateNode("B");
                for (int i = 0; i < 4; i++)
                {
                    var r = tx.CreateRelationship(a, b, "R");
                    tx.SetProperty(r, "w", PropertyValue.FromInt64(10));
                }
                tx.Commit();
            }
            db.CreateColumn(EntityKind.Relationship, "w").Should().BeTrue();
            using (var tx = db.BeginTransaction())
            {
                foreach (var r in AllRels(db)) tx.SetProperty(r, "w", PropertyValue.FromInt64(15));
                tx.Commit();
            }
        }

        // 再オープン (clean close → recovery)。列の head ページ + catalog が永続している。
        using (var db2 = GraphDatabase.Open(_path))
        {
            db2.CreateColumn(EntityKind.Relationship, "w").Should().BeFalse(); // 登録永続
            using var tx = db2.BeginReadOnlyTransaction();
            tx.G(db2.Schema).Relationships().ToList().Should().HaveCount(4);
            // 列スキャン集約は committed 最新値 (15 × 4 = 60)。
            tx.G(db2.Schema).Relationships().SumLong("w").Should().Be(60);
        }
    }

    [Fact]
    public void Column_data_survives_snapshot_recovery()
    {
        // CreateColumn の構築を WAL 文脈下で commit するため、online backup (CreateSnapshot) の
        // ターゲットも WAL redo で列 head ページ + catalog + 列テナント page-table を復元できる。
        var snapPath = Path.Combine(_dir, "snap.quiver");
        using (var db = GraphDatabase.Open(_path))
        {
            using (var tx = db.BeginTransaction())
            {
                var a = tx.CreateNode("A");
                var b = tx.CreateNode("B");
                for (int i = 0; i < 4; i++)
                {
                    var r = tx.CreateRelationship(a, b, "R");
                    tx.SetProperty(r, "w", PropertyValue.FromInt64(10));
                }
                tx.Commit();
            }
            db.CreateColumn(EntityKind.Relationship, "w").Should().BeTrue();
            db.CreateSnapshot(snapPath);
        }

        using (var snap = GraphDatabase.Open(snapPath))
        {
            snap.CreateColumn(EntityKind.Relationship, "w").Should().BeFalse(); // 登録 redo 済
            using var tx = snap.BeginReadOnlyTransaction();
            tx.G(snap.Schema).Relationships().ToList().Should().HaveCount(4);
            tx.G(snap.Schema).Relationships().SumLong("w").Should().Be(40); // 10 × 4
        }
    }

    private static List<RelationshipId> AllRels(GraphDatabase db)
    {
        using var tx = db.BeginReadOnlyTransaction();
        return tx.G(db.Schema).Relationships().ToList();
    }
}
