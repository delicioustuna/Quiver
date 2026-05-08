using FluentAssertions;
using GraphDb.Engine.Core;
using GraphDb.Engine.Operators;
using GraphDb.Engine.Stores;
using GraphDb.Engine.Transactions;
using Xunit;

namespace GraphDb.Engine.Operators.Tests;

public class OperatorTests
{
    [Fact]
    public void TupleSchema_IndexOf_returns_correct_index()
    {
        var schema = new TupleSchema([
            new ColumnDefinition("nodeId", TupleSlotType.NodeId),
            new ColumnDefinition("name", TupleSlotType.Utf8String),
        ]);
        schema.IndexOf("nodeId").Should().Be(0);
        schema.IndexOf("name").Should().Be(1);
        schema.IndexOf("missing").Should().Be(-1);
    }

    [Fact]
    public void LiteralProvider_Int64_provides_correct_slot()
    {
        var p = LiteralProvider.Int64(42L);
        var empty = new TupleRef(Span<TupleSlot>.Empty);
        var slot = p.Provide(in empty, null!);
        slot.Type.Should().Be(TupleSlotType.Int64);
        slot.LongValue.Should().Be(42L);
    }

    [Fact]
    public void LiteralProvider_String_provides_utf8_bytes()
    {
        var p = LiteralProvider.String("Hello");
        var empty = new TupleRef(Span<TupleSlot>.Empty);
        var bytes = p.ProvideBytes(in empty, null!);
        System.Text.Encoding.UTF8.GetString(bytes).Should().Be("Hello");
    }

    [Fact]
    public void LimitOperator_stops_at_limit()
    {
        var source = new FixedNodeListOperator([
            new NodeId(1), new NodeId(2), new NodeId(3), new NodeId(4), new NodeId(5)]);
        using var limit = new LimitOperator(source, limit: 3);
        limit.Open(null!);

        var collected = Collect(limit);
        collected.Should().Equal([1, 2, 3]);
    }

    [Fact]
    public void LimitOperator_skip_works()
    {
        var source = new FixedNodeListOperator([
            new NodeId(1), new NodeId(2), new NodeId(3), new NodeId(4)]);
        using var limit = new LimitOperator(source, limit: 2, skip: 1);
        limit.Open(null!);

        Collect(limit).Should().Equal([2, 3]);
    }

    [Fact]
    public void LimitOperator_empty_source_returns_empty()
    {
        var source = new FixedNodeListOperator([]);
        using var limit = new LimitOperator(source, limit: 5);
        limit.Open(null!);
        Collect(limit).Should().BeEmpty();
    }

    [Fact]
    public void FilterOperator_removes_odd_nodeids()
    {
        var source = new FixedNodeListOperator([
            new NodeId(1), new NodeId(2), new NodeId(3), new NodeId(4)]);
        using var filter = new FilterOperator(source, new EvenNodeIdPredicate());
        filter.Open(null!);

        Collect(filter).Should().Equal([2, 4]);
    }

    [Fact]
    public void FilterOperator_empty_when_no_match()
    {
        var source = new FixedNodeListOperator([new NodeId(1), new NodeId(3)]);
        using var filter = new FilterOperator(source, new EvenNodeIdPredicate());
        filter.Open(null!);
        Collect(filter).Should().BeEmpty();
    }

    [Fact]
    public void ProjectOperator_doubles_values()
    {
        var source = new FixedNodeListOperator([new NodeId(10), new NodeId(20)]);
        using var project = new ProjectOperator(source, [new ProjectionSpec("doubled", new DoubleValueCompute())]);
        project.Open(null!);

        Collect(project).Should().Equal([20, 40]);
    }

    [Fact]
    public void ProjectOperator_schema_has_output_column_names()
    {
        var source = new FixedNodeListOperator([]);
        using var project = new ProjectOperator(source, [
            new ProjectionSpec("a", new DoubleValueCompute()),
            new ProjectionSpec("b", new DoubleValueCompute()),
        ]);
        project.Schema.Columns.Should().HaveCount(2);
        project.Schema.Columns[0].Name.Should().Be("a");
        project.Schema.Columns[1].Name.Should().Be("b");
    }

    [Fact]
    public void LimitOperator_statistics_count_rows()
    {
        var source = new FixedNodeListOperator([new NodeId(1), new NodeId(2), new NodeId(3)]);
        using var limit = new LimitOperator(source, limit: 2);
        limit.Open(null!);
        while (limit.MoveNext()) { }
        limit.Statistics.RowsProduced.Should().Be(2);
    }

    [Fact]
    public void ExpandOperator_schema_NeighborOnly_has_one_column()
    {
        var src = new FixedNodeListOperator([]);
        var expand = new ExpandOperator(src, 0, Direction.Both, null, ExpandOutputMode.NeighborOnly);
        expand.Schema.Columns.Should().HaveCount(1);
        expand.Schema.Columns[0].Name.Should().Be("neighbor");
        expand.Dispose();
    }

    [Fact]
    public void ExpandOperator_schema_Full_has_three_columns()
    {
        var src = new FixedNodeListOperator([]);
        var expand = new ExpandOperator(src, 0, Direction.Both, null, ExpandOutputMode.Full);
        expand.Schema.Columns.Should().HaveCount(3);
        expand.Schema.Columns.Select(c => c.Name).Should().Equal("source", "rel", "neighbor");
        expand.Dispose();
    }

    [Fact]
    public void Filter_then_Limit_pipeline()
    {
        var source = new FixedNodeListOperator([
            new NodeId(1), new NodeId(2), new NodeId(3), new NodeId(4), new NodeId(5), new NodeId(6)]);
        var filter = new FilterOperator(source, new EvenNodeIdPredicate());
        using var limit = new LimitOperator(filter, limit: 2);
        limit.Open(null!);

        Collect(limit).Should().Equal([2, 4]);
    }

    // ---- helpers ----

    private static List<long> Collect(IPhysicalOperator op)
    {
        var result = new List<long>();
        while (op.MoveNext())
            result.Add(op.Current[0].LongValue);
        return result;
    }
}

// ---- Test doubles ----

internal sealed class FixedNodeListOperator : IPhysicalOperator
{
    private readonly NodeId[] _nodes;
    private int _index = -1;
    private readonly TupleSlot[] _buffer = new TupleSlot[1];

    public FixedNodeListOperator(NodeId[] nodes) => _nodes = nodes;

    public TupleSchema Schema { get; } = new([new ColumnDefinition("nodeId", TupleSlotType.NodeId)]);
    public OperatorStatistics Statistics => default;
    public TupleRef Current => new(_buffer);

    public void Open(ITransaction tx) { _index = -1; }
    public bool MoveNext()
    {
        if (++_index >= _nodes.Length) return false;
        _buffer[0] = new TupleSlot { Type = TupleSlotType.NodeId, LongValue = _nodes[_index].Value };
        return true;
    }
    public void Dispose() { }
}

internal sealed class EvenNodeIdPredicate : IPredicate
{
    public bool Evaluate(in TupleRef tuple, ITransaction tx) => tuple[0].LongValue % 2 == 0;
}

internal sealed class DoubleValueCompute : IProjectionCompute
{
    public TupleSlot Compute(in TupleRef tuple, ITransaction tx)
        => new TupleSlot { Type = TupleSlotType.Int64, LongValue = tuple[0].LongValue * 2 };
}
