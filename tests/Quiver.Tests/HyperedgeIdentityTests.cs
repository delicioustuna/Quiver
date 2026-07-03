using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

public sealed class HyperedgeIdentityTests
{
    [Fact]
    public void HyperedgeId_has_same_sequence_generation_contract_as_other_entity_ids()
    {
        var id = HyperedgeId.Create(42, 7);

        id.IsValid.Should().BeTrue();
        id.Sequence.Should().Be(42);
        id.Generation.Should().Be(7);
        id.Should().Be(HyperedgeId.Create(42, 8), "entity ID equality is sequence based");
        id.GetHashCode().Should().Be(HyperedgeId.Create(42, 8).GetHashCode());
        HyperedgeId.Invalid.IsValid.Should().BeFalse();
        HyperedgeId.Invalid.Sequence.Should().Be(-1);
    }

    [Fact]
    public void Hyperedge_and_role_token_ids_have_invalid_sentinels()
    {
        HyperedgeTypeId.Invalid.IsValid.Should().BeFalse();
        new HyperedgeTypeId(0).IsValid.Should().BeTrue();
        RoleId.Invalid.IsValid.Should().BeFalse();
        new RoleId(0).IsValid.Should().BeTrue();
    }

    [Fact]
    public void IncidenceId_has_internal_sequence_sentinel()
    {
        IncidenceId.Invalid.IsValid.Should().BeFalse();
        var first = new IncidenceId(0);
        first.IsValid.Should().BeTrue();
        first.Sequence.Should().Be(0);
    }

    [Fact]
    public void EntityId_from_hyperedge_round_trips()
    {
        var hyperedge = HyperedgeId.Create(123, 4);
        var entity = EntityId.FromHyperedge(hyperedge);

        entity.Kind.Should().Be(EntityKind.Hyperedge);
        entity.LocalId.Should().Be(hyperedge.Value);
        entity.AsHyperedge().Should().Be(hyperedge);
        EntityId.FromPacked(entity.ToPacked()).Should().Be(entity);
    }

    [Fact]
    public void EntityId_as_hyperedge_rejects_other_kinds()
    {
        var entity = EntityId.FromNode(new NodeId(1));
        var act = () => entity.AsHyperedge();
        act.Should().Throw<InvalidOperationException>();
    }
}
