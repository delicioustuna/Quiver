using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Optimizer;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public sealed class NexusOperatorTests
{
    [Fact]
    public void AllNexusesScan_emits_visible_nexuses_and_type_filter()
    {
        NexusId fact = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            var a = tx.CreateVertex("N");
            var b = tx.CreateVertex("N");
            fact = tx.CreateNexus("Fact", [new("Subject", a), new("Object", b)]);
            tx.CreateNexus("Event", [new("Actor", a), new("Target", b)]);
        });

        fx.Db.Schema.TryGetNexusTypeId("Fact", out var factType).Should().BeTrue();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new AllNexusesScanOperator(factType));

        result.Schema.Columns.Single().Type.Should().Be(TupleSlotType.NexusId);
        result.Rows().Select(row => row.GetNexusId(0)).Should().Equal(fact);
        tx.Rollback();
    }

    [Fact]
    public void ExpandToNexus_filters_type_and_role_and_preserves_source()
    {
        VertexId source = default;
        NexusId fact = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            source = tx.CreateVertex("N");
            var b = tx.CreateVertex("N");
            var c = tx.CreateVertex("N");
            fact = tx.CreateNexus("Fact", [new("Subject", source), new("Object", b)]);
            tx.CreateNexus("Event", [new("Actor", source), new("Target", c)]);
        });

        fx.Db.Schema.TryGetNexusTypeId("Fact", out var factType).Should().BeTrue();
        var roles = (INexusSchemaResolver)fx.Db.Schema;
        roles.TryGetRoleId("Subject", out var subjectRole).Should().BeTrue();

        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new ExpandToNexusOperator(
            new FixedVertexListOperator(source),
            sourceVertexColumn: 0,
            factType,
            subjectRole,
            carryColumns: [0]));

        var row = result.Rows().Single();
        row.GetVertexId(0).Should().Be(source);
        row.GetNexusId(1).Should().Be(fact);
        row.GetVertexId(2).Should().Be(source);
        result.Schema.Columns.Select(c => c.Type).Should().Equal(
            TupleSlotType.VertexId, TupleSlotType.NexusId, TupleSlotType.VertexId);
        tx.Rollback();
    }

    [Fact]
    public void ExpandMembers_filters_role_and_excludes_origin_vertex()
    {
        VertexId source = default;
        VertexId target = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            source = tx.CreateVertex("N");
            target = tx.CreateVertex("N");
            tx.CreateNexus("Fact", [
                new("Subject", source),
                new("Object", target),
            ]);
        });

        using var tx = fx.Db.BeginTransaction();
        var toNexus = new ExpandToNexusOperator(
            new FixedVertexListOperator(source), 0, null, null);
        using var result = tx.Execute(new ExpandMembersOperator(
            toNexus,
            nexusColumn: 1,
            roleFilter: null,
            excludeVertexColumn: 0,
            carryColumns: [0]));

        var row = result.Rows().Single();
        row.GetVertexId(1).Should().Be(target);
        row.GetVertexId(2).Should().Be(source);
        tx.Rollback();
    }

    [Fact]
    public void ExpandMembers_role_filter_returns_only_matching_member()
    {
        NexusId nexus = default;
        VertexId target = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            var source = tx.CreateVertex("N");
            target = tx.CreateVertex("N");
            nexus = tx.CreateNexus("Fact", [
                new("Subject", source),
                new("Object", target),
            ]);
        });

        var roles = (INexusSchemaResolver)fx.Db.Schema;
        roles.TryGetRoleId("Object", out var objectRole).Should().BeTrue();

        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new ExpandMembersOperator(
            new FixedNexusListOperator(nexus),
            nexusColumn: 0,
            objectRole,
            excludeVertexColumn: null));

        result.Rows().Select(row => row.GetVertexId(1)).Should().Equal(target);
        tx.Rollback();
    }

    [Fact]
    public void Planner_resolves_unknown_role_to_empty_result_without_creating_token()
    {
        VertexId source = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            source = tx.CreateVertex("N");
            var target = tx.CreateVertex("N");
            tx.CreateNexus("Fact", [new("Subject", source), new("Object", target)]);
        });

        var logical = new ExpandToNexusOp(
            new VertexSeedOp([source]), 0, null, "MissingRole", null);
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(PhysicalPlanner.Plan(logical, fx.Db.Schema));

        result.Rows().Should().BeEmpty();
        fx.Db.Schema.ListRoles().Should().NotContain("MissingRole");
        tx.Rollback();
    }

    [Fact]
    public void Snapshot_started_before_create_does_not_see_new_nexus()
    {
        VertexId a = default;
        VertexId b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateVertex("N");
            b = tx.CreateVertex("N");
        });

        using var snapshot = fx.Db.BeginReadOnlyTransaction();
        using (var writer = fx.Db.BeginTransaction())
        {
            writer.CreateNexus("Fact", [new("Subject", a), new("Object", b)]);
            writer.Commit();
        }

        using var result = snapshot.Execute(new AllNexusesScanOperator());
        result.Rows().Should().BeEmpty();
        snapshot.Rollback();
    }

    [Fact]
    public void ExpandToNexus_MoveNext_has_no_per_row_managed_allocation()
    {
        VertexId source = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            source = tx.CreateVertex("N");
            for (int i = 0; i < 64; i++)
            {
                var target = tx.CreateVertex("N");
                tx.CreateNexus("Fact", [new("Subject", source), new("Object", target)]);
            }
        });

        using var tx = fx.Db.BeginTransaction();
        using (var warmup = new ExpandToNexusOperator(
            new FixedVertexListOperator(source), 0, null, null))
        {
            warmup.Open(((GraphTransaction)tx).Inner);
            while (warmup.MoveNext()) { }
        }

        using var op = new ExpandToNexusOperator(
            new FixedVertexListOperator(source), 0, null, null);
        op.Open(((GraphTransaction)tx).Inner);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = 0;
        while (op.MoveNext()) count++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        count.Should().Be(64);
        allocated.Should().Be(0);
        tx.Rollback();
    }

    [Fact]
    public void NexusScan_and_ExpandMembers_MoveNext_have_no_per_row_managed_allocation()
    {
        NexusId nexus = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            var members = new NexusMember[64];
            for (int i = 0; i < members.Length; i++)
                members[i] = new NexusMember($"Role{i}", tx.CreateVertex("N"));
            nexus = tx.CreateNexus("Fact", members);
            for (int i = 1; i < 64; i++)
            {
                var a = tx.CreateVertex("N");
                var b = tx.CreateVertex("N");
                tx.CreateNexus("Fact", [new("A", a), new("B", b)]);
            }
        });

        using var tx = fx.Db.BeginTransaction();
        using (var warmScan = new AllNexusesScanOperator())
        {
            warmScan.Open(((GraphTransaction)tx).Inner);
            while (warmScan.MoveNext()) { }
        }
        using (var warmMembers = new ExpandMembersOperator(
            new FixedNexusListOperator(nexus), 0, null, null))
        {
            warmMembers.Open(((GraphTransaction)tx).Inner);
            while (warmMembers.MoveNext()) { }
        }

        using var scan = new AllNexusesScanOperator();
        scan.Open(((GraphTransaction)tx).Inner);
        long scanBefore = GC.GetAllocatedBytesForCurrentThread();
        int scanCount = 0;
        while (scan.MoveNext()) scanCount++;
        long scanAllocated = GC.GetAllocatedBytesForCurrentThread() - scanBefore;

        using var membersOp = new ExpandMembersOperator(
            new FixedNexusListOperator(nexus), 0, null, null);
        membersOp.Open(((GraphTransaction)tx).Inner);
        long membersBefore = GC.GetAllocatedBytesForCurrentThread();
        int memberCount = 0;
        while (membersOp.MoveNext()) memberCount++;
        long membersAllocated = GC.GetAllocatedBytesForCurrentThread() - membersBefore;

        scanCount.Should().Be(64);
        scanAllocated.Should().Be(0);
        memberCount.Should().Be(64);
        membersAllocated.Should().Be(0);
        tx.Rollback();
    }

    private sealed class FixedNexusListOperator(params NexusId[] nexuses)
        : IPhysicalOperator
    {
        private int _index = -1;
        private readonly TupleSlot[] _buffer = new TupleSlot[1];

        public TupleSchema Schema { get; } =
            new([new ColumnDefinition("nexusId", TupleSlotType.NexusId)]);

        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);

        public void Open(Quiver.Transactions.ITransaction tx) => _index = -1;

        public bool MoveNext()
        {
            if (++_index >= nexuses.Length) return false;
            _buffer[0] = new TupleSlot
            {
                Type = TupleSlotType.NexusId,
                LongValue = nexuses[_index].Value,
            };
            return true;
        }

        public void Dispose() { }
    }
}
