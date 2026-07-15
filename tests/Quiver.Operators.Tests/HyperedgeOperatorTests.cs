using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Logical;
using Quiver.Query.Optimizer;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Xunit;

namespace Quiver.Query.Physical.Tests;

public sealed class HyperedgeOperatorTests
{
    [Fact]
    public void AllHyperedgesScan_emits_visible_hyperedges_and_type_filter()
    {
        HyperedgeId fact = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            var a = tx.CreateNode("N");
            var b = tx.CreateNode("N");
            fact = tx.CreateHyperedge("Fact", [new("Subject", a), new("Object", b)]);
            tx.CreateHyperedge("Event", [new("Actor", a), new("Target", b)]);
        });

        fx.Db.Schema.TryGetHyperedgeTypeId("Fact", out var factType).Should().BeTrue();
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new AllHyperedgesScanOperator(factType));

        result.Schema.Columns.Single().Type.Should().Be(TupleSlotType.HyperedgeId);
        result.Rows().Select(row => row.GetHyperedgeId(0)).Should().Equal(fact);
        tx.Rollback();
    }

    [Fact]
    public void ExpandToHyperedge_filters_type_and_role_and_preserves_source()
    {
        NodeId source = default;
        HyperedgeId fact = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            source = tx.CreateNode("N");
            var b = tx.CreateNode("N");
            var c = tx.CreateNode("N");
            fact = tx.CreateHyperedge("Fact", [new("Subject", source), new("Object", b)]);
            tx.CreateHyperedge("Event", [new("Actor", source), new("Target", c)]);
        });

        fx.Db.Schema.TryGetHyperedgeTypeId("Fact", out var factType).Should().BeTrue();
        var roles = (IHyperedgeSchemaResolver)fx.Db.Schema;
        roles.TryGetRoleId("Subject", out var subjectRole).Should().BeTrue();

        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new ExpandToHyperedgeOperator(
            new FixedNodeListOperator(source),
            sourceNodeColumn: 0,
            factType,
            subjectRole,
            carryColumns: [0]));

        var row = result.Rows().Single();
        row.GetNodeId(0).Should().Be(source);
        row.GetHyperedgeId(1).Should().Be(fact);
        row.GetNodeId(2).Should().Be(source);
        result.Schema.Columns.Select(c => c.Type).Should().Equal(
            TupleSlotType.NodeId, TupleSlotType.HyperedgeId, TupleSlotType.NodeId);
        tx.Rollback();
    }

    [Fact]
    public void ExpandMembers_filters_role_and_excludes_origin_node()
    {
        NodeId source = default;
        NodeId target = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            source = tx.CreateNode("N");
            target = tx.CreateNode("N");
            tx.CreateHyperedge("Fact", [
                new("Subject", source),
                new("Object", target),
            ]);
        });

        using var tx = fx.Db.BeginTransaction();
        var toHyperedge = new ExpandToHyperedgeOperator(
            new FixedNodeListOperator(source), 0, null, null);
        using var result = tx.Execute(new ExpandMembersOperator(
            toHyperedge,
            hyperedgeColumn: 1,
            roleFilter: null,
            excludeNodeColumn: 0,
            carryColumns: [0]));

        var row = result.Rows().Single();
        row.GetNodeId(1).Should().Be(target);
        row.GetNodeId(2).Should().Be(source);
        tx.Rollback();
    }

    [Fact]
    public void ExpandMembers_role_filter_returns_only_matching_member()
    {
        HyperedgeId hyperedge = default;
        NodeId target = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            var source = tx.CreateNode("N");
            target = tx.CreateNode("N");
            hyperedge = tx.CreateHyperedge("Fact", [
                new("Subject", source),
                new("Object", target),
            ]);
        });

        var roles = (IHyperedgeSchemaResolver)fx.Db.Schema;
        roles.TryGetRoleId("Object", out var objectRole).Should().BeTrue();

        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(new ExpandMembersOperator(
            new FixedHyperedgeListOperator(hyperedge),
            hyperedgeColumn: 0,
            objectRole,
            excludeNodeColumn: null));

        result.Rows().Select(row => row.GetNodeId(1)).Should().Equal(target);
        tx.Rollback();
    }

    [Fact]
    public void Planner_resolves_unknown_role_to_empty_result_without_creating_token()
    {
        NodeId source = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            source = tx.CreateNode("N");
            var target = tx.CreateNode("N");
            tx.CreateHyperedge("Fact", [new("Subject", source), new("Object", target)]);
        });

        var logical = new ExpandToHyperedgeOp(
            new NodeSeedOp([source]), 0, null, "MissingRole", null);
        using var tx = fx.Db.BeginTransaction();
        using var result = tx.Execute(PhysicalPlanner.Plan(logical, fx.Db.Schema));

        result.Rows().Should().BeEmpty();
        fx.Db.Schema.ListRoles().Should().NotContain("MissingRole");
        tx.Rollback();
    }

    [Fact]
    public void Snapshot_started_before_create_does_not_see_new_hyperedge()
    {
        NodeId a = default;
        NodeId b = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            a = tx.CreateNode("N");
            b = tx.CreateNode("N");
        });

        using var snapshot = fx.Db.BeginReadOnlyTransaction();
        using (var writer = fx.Db.BeginTransaction())
        {
            writer.CreateHyperedge("Fact", [new("Subject", a), new("Object", b)]);
            writer.Commit();
        }

        using var result = snapshot.Execute(new AllHyperedgesScanOperator());
        result.Rows().Should().BeEmpty();
        snapshot.Rollback();
    }

    [Fact]
    public void ExpandToHyperedge_MoveNext_has_no_per_row_managed_allocation()
    {
        NodeId source = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            source = tx.CreateNode("N");
            for (int i = 0; i < 64; i++)
            {
                var target = tx.CreateNode("N");
                tx.CreateHyperedge("Fact", [new("Subject", source), new("Object", target)]);
            }
        });

        using var tx = fx.Db.BeginTransaction();
        using (var warmup = new ExpandToHyperedgeOperator(
            new FixedNodeListOperator(source), 0, null, null))
        {
            warmup.Open(((GraphTransaction)tx).Inner);
            while (warmup.MoveNext()) { }
        }

        using var op = new ExpandToHyperedgeOperator(
            new FixedNodeListOperator(source), 0, null, null);
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
    public void HyperedgeScan_and_ExpandMembers_MoveNext_have_no_per_row_managed_allocation()
    {
        HyperedgeId hyperedge = default;
        using var fx = OperatorTestFixture.Open(tx =>
        {
            var members = new HyperedgeMember[64];
            for (int i = 0; i < members.Length; i++)
                members[i] = new HyperedgeMember($"Role{i}", tx.CreateNode("N"));
            hyperedge = tx.CreateHyperedge("Fact", members);
            for (int i = 1; i < 64; i++)
            {
                var a = tx.CreateNode("N");
                var b = tx.CreateNode("N");
                tx.CreateHyperedge("Fact", [new("A", a), new("B", b)]);
            }
        });

        using var tx = fx.Db.BeginTransaction();
        using (var warmScan = new AllHyperedgesScanOperator())
        {
            warmScan.Open(((GraphTransaction)tx).Inner);
            while (warmScan.MoveNext()) { }
        }
        using (var warmMembers = new ExpandMembersOperator(
            new FixedHyperedgeListOperator(hyperedge), 0, null, null))
        {
            warmMembers.Open(((GraphTransaction)tx).Inner);
            while (warmMembers.MoveNext()) { }
        }

        using var scan = new AllHyperedgesScanOperator();
        scan.Open(((GraphTransaction)tx).Inner);
        long scanBefore = GC.GetAllocatedBytesForCurrentThread();
        int scanCount = 0;
        while (scan.MoveNext()) scanCount++;
        long scanAllocated = GC.GetAllocatedBytesForCurrentThread() - scanBefore;

        using var membersOp = new ExpandMembersOperator(
            new FixedHyperedgeListOperator(hyperedge), 0, null, null);
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

    private sealed class FixedHyperedgeListOperator(params HyperedgeId[] hyperedges)
        : IPhysicalOperator
    {
        private int _index = -1;
        private readonly TupleSlot[] _buffer = new TupleSlot[1];

        public TupleSchema Schema { get; } =
            new([new ColumnDefinition("hyperedgeId", TupleSlotType.HyperedgeId)]);

        public OperatorStatistics Statistics => default;
        public TupleRef Current => new(_buffer);

        public void Open(Quiver.Transactions.ITransaction tx) => _index = -1;

        public bool MoveNext()
        {
            if (++_index >= hyperedges.Length) return false;
            _buffer[0] = new TupleSlot
            {
                Type = TupleSlotType.HyperedgeId,
                LongValue = hyperedges[_index].Value,
            };
            return true;
        }

        public void Dispose() { }
    }
}
