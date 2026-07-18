using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class ShortestPathOperatorTests
{
    [Fact]
    public void Empty_source_yields_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        using var tx = fx.Db.BeginWriteTransaction();
        using var op = new ShortestPathOperator(
            new FixedVertexListOperator(), 0, 0, Direction.Both, null);
        // PairSource は空だが、入力には 1 列だけの FixedVertexListOperator を使っている。
        // 空入力では行を生成せずに終了する。
        using var result = tx.Execute(op);
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Single_hop_path_found()
    {
        VertexId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            b = tx.CreateVertex("X");
            tx.CreateEdge(a, b, "K");
        });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new ShortestPathOperator(
            new PairSourceOperator(a, b), 0, 1, Direction.Outgoing, null));
        result.Rows().Should().HaveCount(1);
        tx2.Rollback();
    }

    [Fact]
    public void No_path_yields_empty()
    {
        VertexId a = default, b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            b = tx.CreateVertex("X"); // disconnected
        });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new ShortestPathOperator(
            new PairSourceOperator(a, b), 0, 1, Direction.Outgoing, null));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    [Fact]
    public void Self_pair_returns_zero_length_path()
    {
        VertexId a = default;
        using var fx = OperatorTestFixture.Open(tx => { a = tx.CreateVertex("X"); });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new ShortestPathOperator(
            new PairSourceOperator(a, a), 0, 1, Direction.Outgoing, null));
        result.Rows().Should().HaveCount(1);
        tx2.Rollback();
    }

    [Fact]
    public void MaxDistance_blocks_path_beyond_limit()
    {
        VertexId a = default, c = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("X");
            var b = tx.CreateVertex("X");
            c = tx.CreateVertex("X");
            tx.CreateEdge(a, b, "K");
            tx.CreateEdge(b, c, "K");
        });
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(new ShortestPathOperator(
            new PairSourceOperator(a, c), 0, 1, Direction.Outgoing, null, maxDistance: 1));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }
}
