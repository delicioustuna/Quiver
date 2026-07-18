using FluentAssertions;
using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 読み取りとオプティマイザーを統合した列集約のエンドツーエンドテスト。
/// 全走査集約 (<c>g.Vertices()</c> / <c>g.Edges()</c> の
/// Sum、SumLong、Mean、Max、Min) が列化済みキーでは列スキャンを使い、
/// 行経路と同じ結果になることを列の削除前後で確認する。
/// フィルター付き入力は行経路へフォールバックする。
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
    public void Vertex_int_aggregation_column_path_equals_row_path()
    {
        using var db = QuiverDatabase.Open(_path);
        using (var tx = db.BeginWriteTransaction())
        {
            foreach (var v in new[] { 3, 7, 11, 20 })
            {
                var n = tx.CreateVertex("Person");
                tx.SetProperty(n, "score", PropertyValue.FromInt64(v));
            }
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Vertex, "score").Should().BeTrue();

        // 列あり (= 列スキャン経路)。
        (long colSum, double colSumD, double? colMax, double? colMin, double? colMean) = VertexAgg(db, "score");
        colSum.Should().Be(41);
        colSumD.Should().Be(41);
        colMax.Should().Be(20);
        colMin.Should().Be(3);
        colMean.Should().Be(41.0 / 4);

        // 列を外す → row path。結果は一致しなければならない。
        db.DropColumn(EntityKind.Vertex, "score").Should().BeTrue();
        (long rowSum, double rowSumD, double? rowMax, double? rowMin, double? rowMean) = VertexAgg(db, "score");
        rowSum.Should().Be(colSum);
        rowSumD.Should().Be(colSumD);
        rowMax.Should().Be(colMax);
        rowMin.Should().Be(colMin);
        rowMean.Should().Be(colMean);
    }

    [Fact]
    public void Vertex_double_aggregation_column_path_equals_row_path()
    {
        using var db = QuiverDatabase.Open(_path);
        using (var tx = db.BeginWriteTransaction())
        {
            foreach (var v in new[] { 1.5, 2.25, -0.75 })
            {
                var n = tx.CreateVertex("M");
                tx.SetProperty(n, "w", PropertyValue.FromDouble(v));
            }
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Vertex, "w").Should().BeTrue();
        var col = VertexAgg(db, "w");
        col.SumD.Should().BeApproximately(3.0, 1e-9);
        col.Max.Should().Be(2.25);
        col.Min.Should().Be(-0.75);

        db.DropColumn(EntityKind.Vertex, "w").Should().BeTrue();
        var row = VertexAgg(db, "w");
        row.SumD.Should().BeApproximately(col.SumD, 1e-9);
        row.Max.Should().Be(col.Max);
        row.Min.Should().Be(col.Min);
        row.Mean.Should().BeApproximately(col.Mean!.Value, 1e-9);
    }

    [Fact]
    public void Edge_aggregation_column_path_equals_row_path()
    {
        using var db = QuiverDatabase.Open(_path);
        using (var tx = db.BeginWriteTransaction())
        {
            var a = tx.CreateVertex("A");
            var b = tx.CreateVertex("B");
            foreach (var w in new[] { 10L, 25L, 5L })
            {
                var r = tx.CreateEdge(a, b, "R");
                tx.SetProperty(r, "weight", PropertyValue.FromInt64(w));
            }
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Edge, "weight").Should().BeTrue();

        using (var tx = db.BeginReadTransaction())
        {
            var g = tx.Query;
            g.Edges().SumLong("weight").Should().Be(40);
            g.Edges().Max("weight").Should().Be(25);
            g.Edges().ToList().Should().HaveCount(3); // 全 edge 列挙
        }

        db.DropColumn(EntityKind.Edge, "weight").Should().BeTrue();
        using (var tx = db.BeginReadTransaction())
        {
            var g = tx.Query;
            g.Edges().SumLong("weight").Should().Be(40); // row path 同値
            g.Edges().Max("weight").Should().Be(25);
        }
    }

    [Fact]
    public void Filtered_input_falls_back_to_row_path_and_is_correct()
    {
        using var db = QuiverDatabase.Open(_path);
        using (var tx = db.BeginWriteTransaction())
        {
            var p1 = tx.CreateVertex("Person"); tx.SetProperty(p1, "age", PropertyValue.FromInt64(30));
            var p2 = tx.CreateVertex("Person"); tx.SetProperty(p2, "age", PropertyValue.FromInt64(40));
            var c1 = tx.CreateVertex("Company"); tx.SetProperty(c1, "age", PropertyValue.FromInt64(100));
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Vertex, "age").Should().BeTrue();

        using var tx2 = db.BeginReadTransaction();
        var g = tx2.Query;
        // 全件 (列スキャン): 30+40+100 = 170。
        g.Vertices().SumLong("age").Should().Be(170);
        // label フィルタ付き (row path フォールバック): Person のみ 70。
        g.Vertices().HasLabel("Person").SumLong("age").Should().Be(70);
    }

    [Fact]
    public void Maintained_writes_reflect_in_column_aggregation()
    {
        using var db = QuiverDatabase.Open(_path);
        db.CreateColumn(EntityKind.Vertex, "score").Should().BeTrue();
        using (var tx = db.BeginWriteTransaction())
        {
            var n1 = tx.CreateVertex("X"); tx.SetProperty(n1, "score", PropertyValue.FromInt64(100));
            var n2 = tx.CreateVertex("X"); tx.SetProperty(n2, "score", PropertyValue.FromInt64(50));
            tx.Commit();
        }
        using var tx2 = db.BeginReadTransaction();
        tx2.Query.Vertices().SumLong("score").Should().Be(150);
    }

    private static (long Sum, double SumD, double? Max, double? Min, double? Mean) VertexAgg(QuiverDatabase db, string key)
    {
        using var tx = db.BeginReadTransaction();
        var g = tx.Query;
        return (
            g.Vertices().SumLong(key),
            g.Vertices().Sum(key),
            g.Vertices().Max(key),
            g.Vertices().Min(key),
            g.Vertices().Mean(key));
    }
}
