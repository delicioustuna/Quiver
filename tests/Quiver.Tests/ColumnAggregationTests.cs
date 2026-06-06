using FluentAssertions;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// ARCH-5c Phase 5 (5d): read/optimizer 統合の end-to-end 検証。full-scan 集約
/// (g.Nodes()/g.Relationships() の Sum/SumLong/Mean/Max/Min) が列化済み key で列スキャンを
/// 使い、かつ row path と同値であることを「列あり → DropColumn → 列なし(row path)」の
/// 結果一致で確認する。filter 付き入力は row path にフォールバックする。
/// </summary>
public sealed class ColumnAggregationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ColumnAggregationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_colagg_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Node_int_aggregation_column_path_equals_row_path()
    {
        using var db = GraphDatabase.Open(_path);
        using (var tx = db.BeginTransaction())
        {
            foreach (var v in new[] { 3, 7, 11, 20 })
            {
                var n = tx.CreateNode("Person");
                tx.SetProperty(n, "score", PropertyValue.FromInt64(v));
            }
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Node, "score").Should().BeTrue();

        // 列あり (= 列スキャン経路)。
        (long colSum, double colSumD, double? colMax, double? colMin, double? colMean) = NodeAgg(db, "score");
        colSum.Should().Be(41);
        colSumD.Should().Be(41);
        colMax.Should().Be(20);
        colMin.Should().Be(3);
        colMean.Should().Be(41.0 / 4);

        // 列を外す → row path。結果は一致しなければならない。
        db.DropColumn(EntityKind.Node, "score").Should().BeTrue();
        (long rowSum, double rowSumD, double? rowMax, double? rowMin, double? rowMean) = NodeAgg(db, "score");
        rowSum.Should().Be(colSum);
        rowSumD.Should().Be(colSumD);
        rowMax.Should().Be(colMax);
        rowMin.Should().Be(colMin);
        rowMean.Should().Be(colMean);
    }

    [Fact]
    public void Node_double_aggregation_column_path_equals_row_path()
    {
        using var db = GraphDatabase.Open(_path);
        using (var tx = db.BeginTransaction())
        {
            foreach (var v in new[] { 1.5, 2.25, -0.75 })
            {
                var n = tx.CreateNode("M");
                tx.SetProperty(n, "w", PropertyValue.FromDouble(v));
            }
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Node, "w").Should().BeTrue();
        var col = NodeAgg(db, "w");
        col.SumD.Should().BeApproximately(3.0, 1e-9);
        col.Max.Should().Be(2.25);
        col.Min.Should().Be(-0.75);

        db.DropColumn(EntityKind.Node, "w").Should().BeTrue();
        var row = NodeAgg(db, "w");
        row.SumD.Should().BeApproximately(col.SumD, 1e-9);
        row.Max.Should().Be(col.Max);
        row.Min.Should().Be(col.Min);
        row.Mean.Should().BeApproximately(col.Mean!.Value, 1e-9);
    }

    [Fact]
    public void Relationship_aggregation_column_path_equals_row_path()
    {
        using var db = GraphDatabase.Open(_path);
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateNode("A");
            var b = tx.CreateNode("B");
            foreach (var w in new[] { 10L, 25L, 5L })
            {
                var r = tx.CreateRelationship(a, b, "R");
                tx.SetProperty(r, "weight", PropertyValue.FromInt64(w));
            }
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Relationship, "weight").Should().BeTrue();

        using (var tx = db.BeginReadOnlyTransaction())
        {
            var g = tx.G(db.Schema);
            g.Relationships().SumLong("weight").Should().Be(40);
            g.Relationships().Max("weight").Should().Be(25);
            g.Relationships().ToList().Should().HaveCount(3); // 全 rel 列挙
        }

        db.DropColumn(EntityKind.Relationship, "weight").Should().BeTrue();
        using (var tx = db.BeginReadOnlyTransaction())
        {
            var g = tx.G(db.Schema);
            g.Relationships().SumLong("weight").Should().Be(40); // row path 同値
            g.Relationships().Max("weight").Should().Be(25);
        }
    }

    [Fact]
    public void Filtered_input_falls_back_to_row_path_and_is_correct()
    {
        using var db = GraphDatabase.Open(_path);
        using (var tx = db.BeginTransaction())
        {
            var p1 = tx.CreateNode("Person"); tx.SetProperty(p1, "age", PropertyValue.FromInt64(30));
            var p2 = tx.CreateNode("Person"); tx.SetProperty(p2, "age", PropertyValue.FromInt64(40));
            var c1 = tx.CreateNode("Company"); tx.SetProperty(c1, "age", PropertyValue.FromInt64(100));
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Node, "age").Should().BeTrue();

        using var tx2 = db.BeginReadOnlyTransaction();
        var g = tx2.G(db.Schema);
        // 全件 (列スキャン): 30+40+100 = 170。
        g.Nodes().SumLong("age").Should().Be(170);
        // label フィルタ付き (row path フォールバック): Person のみ 70。
        g.Nodes().HasLabel("Person").SumLong("age").Should().Be(70);
    }

    [Fact]
    public void Maintained_writes_reflect_in_column_aggregation()
    {
        using var db = GraphDatabase.Open(_path);
        db.CreateColumn(EntityKind.Node, "score").Should().BeTrue();
        using (var tx = db.BeginTransaction())
        {
            var n1 = tx.CreateNode("X"); tx.SetProperty(n1, "score", PropertyValue.FromInt64(100));
            var n2 = tx.CreateNode("X"); tx.SetProperty(n2, "score", PropertyValue.FromInt64(50));
            tx.Commit();
        }
        using var tx2 = db.BeginReadOnlyTransaction();
        tx2.G(db.Schema).Nodes().SumLong("score").Should().Be(150);
    }

    private static (long Sum, double SumD, double? Max, double? Min, double? Mean) NodeAgg(GraphDatabase db, string key)
    {
        using var tx = db.BeginReadOnlyTransaction();
        var g = tx.G(db.Schema);
        return (
            g.Nodes().SumLong(key),
            g.Nodes().Sum(key),
            g.Nodes().Max(key),
            g.Nodes().Min(key),
            g.Nodes().Mean(key));
    }
}
