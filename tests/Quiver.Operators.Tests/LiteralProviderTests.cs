using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public class LiteralProviderTests
{
    private static TupleRef Empty => new(Span<TupleSlot>.Empty);

    [Fact]
    public void Int64_provides_correct_slot()
    {
        var p = LiteralProvider.Int64(42L);
        var empty = Empty;
        var slot = p.Provide(in empty, null!);
        slot.Type.Should().Be(TupleSlotType.Int64);
        slot.LongValue.Should().Be(42L);
    }

    [Fact]
    public void String_provides_utf8_bytes()
    {
        var p = LiteralProvider.String("Hello");
        var empty = Empty;
        var bytes = p.ProvideBytes(in empty, null!);
        System.Text.Encoding.UTF8.GetString(bytes).Should().Be("Hello");
    }

    [Fact]
    public void Double_round_trips_through_long_storage()
    {
        var p = LiteralProvider.Double(3.14);
        var empty = Empty;
        var slot = p.Provide(in empty, null!);
        slot.Type.Should().Be(TupleSlotType.Double);
        BitConverter.Int64BitsToDouble(slot.LongValue).Should().Be(3.14);
    }

    [Fact]
    public void Bool_emits_one_for_true_zero_for_false()
    {
        var empty = Empty;
        LiteralProvider.Bool(true).Provide(in empty, null!).LongValue.Should().Be(1L);
        LiteralProvider.Bool(false).Provide(in empty, null!).LongValue.Should().Be(0L);
    }

    [Fact]
    public void VertexId_carries_id_in_long_slot()
    {
        var p = LiteralProvider.VertexId(new VertexId(99));
        var empty = Empty;
        var slot = p.Provide(in empty, null!);
        slot.Type.Should().Be(TupleSlotType.VertexId);
        slot.LongValue.Should().Be(99);
    }

    [Fact]
    public void Empty_string_provides_zero_length_bytes()
    {
        var p = LiteralProvider.String("");
        var empty = Empty;
        p.ProvideBytes(in empty, null!).Length.Should().Be(0);
    }
}
