using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

public sealed class EntityIdTests
{
    [Fact]
    public void FromVertex_RoundTrips()
    {
        var vertex = new VertexId(42);
        var entity = EntityId.FromVertex(vertex);

        entity.Kind.Should().Be(EntityKind.Vertex);
        entity.LocalId.Should().Be(42);
        entity.IsValid.Should().BeTrue();
        entity.AsVertex().Should().Be(vertex);
    }

    [Fact]
    public void FromEdge_RoundTrips()
    {
        var edge = new EdgeId(1234);
        var entity = EntityId.FromEdge(edge);

        entity.Kind.Should().Be(EntityKind.Edge);
        entity.LocalId.Should().Be(1234);
        entity.AsEdge().Should().Be(edge);
    }

    [Fact]
    public void FromNexus_RoundTrips()
    {
        var nexus = new NexusId(7);
        var entity = EntityId.FromNexus(nexus);

        entity.Kind.Should().Be(EntityKind.Nexus);
        entity.AsNexus().Should().Be(nexus);
    }

    [Fact]
    public void AsVertex_OnEdge_Throws()
    {
        var entity = EntityId.FromEdge(new EdgeId(1));
        var act = () => entity.AsVertex();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AsEdge_OnVertex_Throws()
    {
        var entity = EntityId.FromVertex(new VertexId(1));
        var act = () => entity.AsEdge();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Invalid_IsNotValid()
    {
        EntityId.Invalid.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Packed_RoundTrips_AcrossKinds()
    {
        var entries = new[]
        {
            EntityId.FromVertex(new VertexId(0)),
            EntityId.FromVertex(new VertexId(1)),
            EntityId.FromVertex(new VertexId(1_000_000_000L)),
            EntityId.FromEdge(new EdgeId(42)),
            EntityId.FromNexus(new NexusId(99)),
        };

        foreach (var e in entries)
        {
            var packed = e.ToPacked();
            var decoded = EntityId.FromPacked(packed);
            decoded.Should().Be(e);
        }
    }

    [Fact]
    public void Packed_Invalid_RoundTrips()
    {
        EntityId.Invalid.ToPacked().Should().Be(0UL);
        EntityId.FromPacked(0UL).Should().Be(EntityId.Invalid);
    }

    [Fact]
    public void Packed_TooLargeLocalId_Throws()
    {
        var entity = new EntityId(EntityKind.Vertex, (1L << 60));
        var act = () => entity.ToPacked();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(3UL << 60)]
    [InlineData(5UL << 60)]
    [InlineData(15UL << 60)]
    public void FromPacked_Rejects_reserved_or_unknown_kind(ulong packed)
    {
        var act = () => EntityId.FromPacked(packed);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData((byte)EntityKind.Vertex, -2L)]
    [InlineData((byte)EntityKind.Vertex, 1L << 60)]
    [InlineData((byte)3, 1L)]
    public void Noncanonical_invalid_entity_id_is_not_packable(byte rawKind, long localId)
    {
        var kind = (EntityKind)rawKind;
        var entity = new EntityId(kind, localId);

        entity.IsValid.Should().BeFalse();
        var act = () => entity.ToPacked();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Typed_invalid_ids_normalize_to_canonical_invalid()
    {
        EntityId.FromVertex(VertexId.Invalid).Should().Be(EntityId.Invalid);
        EntityId.FromEdge(EdgeId.Invalid).Should().Be(EntityId.Invalid);
        EntityId.FromNexus(NexusId.Invalid).Should().Be(EntityId.Invalid);
    }

    [Fact]
    public void Equality_DistinguishesKind()
    {
        var vertexEntity = EntityId.FromVertex(new VertexId(5));
        var relEntity = EntityId.FromEdge(new EdgeId(5));
        vertexEntity.Should().NotBe(relEntity);
    }

    [Fact]
    public void ToString_Formats()
    {
        EntityId.FromVertex(new VertexId(7)).ToString().Should().Be("Vertex#7");
        EntityId.Invalid.ToString().Should().Be("Entity#Invalid");
    }
}
