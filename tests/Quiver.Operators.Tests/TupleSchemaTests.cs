using FluentAssertions;
using Quiver.Query.Physical;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class TupleSchemaTests
{
    [Fact]
    public void IndexOf_returns_correct_index_for_known_column()
    {
        var schema = new TupleSchema([
            new ColumnDefinition("vertexId", TupleSlotType.VertexId),
            new ColumnDefinition("name", TupleSlotType.Utf8String),
        ]);
        schema.IndexOf("vertexId").Should().Be(0);
        schema.IndexOf("name").Should().Be(1);
    }

    [Fact]
    public void IndexOf_returns_negative_for_unknown_column()
    {
        var schema = new TupleSchema([new ColumnDefinition("only", TupleSlotType.VertexId)]);
        schema.IndexOf("missing").Should().Be(-1);
    }

    [Fact]
    public void Empty_schema_has_zero_columns()
    {
        var schema = new TupleSchema([]);
        schema.Columns.Should().BeEmpty();
        schema.IndexOf("any").Should().Be(-1);
    }
}
