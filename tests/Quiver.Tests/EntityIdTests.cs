using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

public sealed class EntityIdTests
{
    [Fact]
    public void FromNode_RoundTrips()
    {
        var node = new NodeId(42);
        var entity = EntityId.FromNode(node);

        entity.Kind.Should().Be(EntityKind.Node);
        entity.LocalId.Should().Be(42);
        entity.IsValid.Should().BeTrue();
        entity.AsNode().Should().Be(node);
    }

    [Fact]
    public void FromRelationship_RoundTrips()
    {
        var rel = new RelationshipId(1234);
        var entity = EntityId.FromRelationship(rel);

        entity.Kind.Should().Be(EntityKind.Relationship);
        entity.LocalId.Should().Be(1234);
        entity.AsRelationship().Should().Be(rel);
    }

    [Fact]
    public void FromProperty_RoundTrips()
    {
        var prop = new PropertyId(7);
        var entity = EntityId.FromProperty(prop);

        entity.Kind.Should().Be(EntityKind.Property);
        entity.AsProperty().Should().Be(prop);
    }

    [Fact]
    public void AsNode_OnRelationship_Throws()
    {
        var entity = EntityId.FromRelationship(new RelationshipId(1));
        var act = () => entity.AsNode();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AsRelationship_OnNode_Throws()
    {
        var entity = EntityId.FromNode(new NodeId(1));
        var act = () => entity.AsRelationship();
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
            EntityId.FromNode(new NodeId(0)),
            EntityId.FromNode(new NodeId(1)),
            EntityId.FromNode(new NodeId(1_000_000_000L)),
            EntityId.FromRelationship(new RelationshipId(42)),
            EntityId.FromProperty(new PropertyId(99)),
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
        var entity = new EntityId(EntityKind.Node, (1L << 60));
        var act = () => entity.ToPacked();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Equality_DistinguishesKind()
    {
        var nodeEntity = EntityId.FromNode(new NodeId(5));
        var relEntity = EntityId.FromRelationship(new RelationshipId(5));
        nodeEntity.Should().NotBe(relEntity);
    }

    [Fact]
    public void ToString_Formats()
    {
        EntityId.FromNode(new NodeId(7)).ToString().Should().Be("Node#7");
        EntityId.Invalid.ToString().Should().Be("Entity#Invalid");
    }
}
