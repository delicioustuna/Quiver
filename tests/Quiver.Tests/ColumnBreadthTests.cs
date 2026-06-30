using FluentAssertions;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// Int32、Int64、Double、Bool の各スカラー型、複数キー、
/// ノードとリレーションシップの列を同時に扱えることを検証する。
/// 数値型の列スキャン集約が行経路と同値であることと、
/// Bool の数値集約が値を保持したまま行経路へフォールバックすることを確認する。
/// </summary>
public sealed class ColumnBreadthTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ColumnBreadthTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_colbreadth_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Int32_column_aggregation_equals_row_path()
    {
        using var db = GraphDatabase.Open(_path);
        db.CreateColumn(EntityKind.Node, "n").Should().BeTrue();
        using (var tx = db.BeginTransaction())
        {
            foreach (var v in new[] { 5, 8, 13 })
            {
                var node = tx.CreateNode("X");
                tx.SetProperty(node, "n", PropertyValue.FromInt32(v));
            }
            tx.Commit();
        }
        using (var tx = db.BeginReadOnlyTransaction())
        {
            tx.G(db.Schema).Nodes().SumLong("n").Should().Be(26);
            tx.G(db.Schema).Nodes().Max("n").Should().Be(13);
        }
        // 列を外しても (row path) 同値。
        db.DropColumn(EntityKind.Node, "n").Should().BeTrue();
        using (var tx = db.BeginReadOnlyTransaction())
        {
            tx.G(db.Schema).Nodes().SumLong("n").Should().Be(26);
            tx.G(db.Schema).Nodes().Max("n").Should().Be(13);
        }
    }

    [Fact]
    public void Multiple_columns_on_same_kind_are_independently_maintained()
    {
        using var db = GraphDatabase.Open(_path);
        db.CreateColumn(EntityKind.Node, "a").Should().BeTrue();
        db.CreateColumn(EntityKind.Node, "b").Should().BeTrue();
        using (var tx = db.BeginTransaction())
        {
            var n1 = tx.CreateNode("X");
            tx.SetProperty(n1, "a", PropertyValue.FromInt64(10));
            tx.SetProperty(n1, "b", PropertyValue.FromInt64(100));
            var n2 = tx.CreateNode("X");
            tx.SetProperty(n2, "a", PropertyValue.FromInt64(20));
            tx.SetProperty(n2, "b", PropertyValue.FromInt64(200));
            tx.Commit();
        }
        using var rtx = db.BeginReadOnlyTransaction();
        var g = rtx.G(db.Schema);
        g.Nodes().SumLong("a").Should().Be(30);
        g.Nodes().SumLong("b").Should().Be(300);
    }

    [Fact]
    public void Node_and_relationship_columns_coexist()
    {
        using var db = GraphDatabase.Open(_path);
        db.CreateColumn(EntityKind.Node, "score").Should().BeTrue();
        db.CreateColumn(EntityKind.Relationship, "weight").Should().BeTrue();
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateNode("A"); tx.SetProperty(a, "score", PropertyValue.FromInt64(7));
            var b = tx.CreateNode("B"); tx.SetProperty(b, "score", PropertyValue.FromInt64(3));
            var r = tx.CreateRelationship(a, b, "R"); tx.SetProperty(r, "weight", PropertyValue.FromDouble(2.5));
            tx.Commit();
        }
        using var rtx = db.BeginReadOnlyTransaction();
        var g = rtx.G(db.Schema);
        g.Nodes().SumLong("score").Should().Be(10);
        g.Relationships().Sum("weight").Should().BeApproximately(2.5, 1e-9);
    }

    [Fact]
    public void Bool_column_is_stored_but_numeric_aggregation_falls_back()
    {
        using var db = GraphDatabase.Open(_path);
        db.CreateColumn(EntityKind.Node, "flag").Should().BeTrue();
        NodeId n;
        using (var tx = db.BeginTransaction())
        {
            n = tx.CreateNode("X");
            tx.SetProperty(n, "flag", PropertyValue.FromBool(true));
            tx.Commit();
        }
        using var rtx = db.BeginReadOnlyTransaction();
        // 値は保持される (point read)。
        rtx.GetProperty(n, "flag").BoolValue.Should().BeTrue();
        // 数値集約は row path フォールバック (bool は数値集約対象外で 0、クラッシュしない)。
        rtx.G(db.Schema).Nodes().Sum("flag").Should().Be(0.0);
        rtx.G(db.Schema).Nodes().Max("flag").Should().BeNull();
    }
}
