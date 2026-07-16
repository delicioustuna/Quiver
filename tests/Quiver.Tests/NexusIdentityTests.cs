using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

public sealed class NexusIdentityTests
{
    [Fact]
    public void NexusId_has_same_sequence_generation_contract_as_other_entity_ids()
    {
        var id = NexusId.Create(42, 7);

        id.IsValid.Should().BeTrue();
        id.Sequence.Should().Be(42);
        id.Generation.Should().Be(7);
        id.Should().NotBe(NexusId.Create(42, 8), "Generation は entity identity の一部である");
        id.GetHashCode().Should().NotBe(NexusId.Create(42, 8).GetHashCode());
        NexusId.Invalid.IsValid.Should().BeFalse();
        NexusId.Invalid.Sequence.Should().Be(-1);
    }

    [Fact]
    public void Nexus_and_role_token_ids_have_invalid_sentinels()
    {
        NexusTypeId.Invalid.IsValid.Should().BeFalse();
        new NexusTypeId(0).IsValid.Should().BeTrue();
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
    public void EntityId_from_nexus_round_trips()
    {
        var nexus = NexusId.Create(123, 4);
        var entity = EntityId.FromNexus(nexus);

        entity.Kind.Should().Be(EntityKind.Nexus);
        entity.LocalId.Should().Be(nexus.Value);
        entity.AsNexus().Should().Be(nexus);
        EntityId.FromPacked(entity.ToPacked()).Should().Be(entity);
    }

    [Fact]
    public void EntityId_as_nexus_rejects_other_kinds()
    {
        var entity = EntityId.FromVertex(new VertexId(1));
        var act = () => entity.AsNexus();
        act.Should().Throw<InvalidOperationException>();
    }
}
