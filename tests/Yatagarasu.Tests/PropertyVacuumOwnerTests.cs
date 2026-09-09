using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Maintenance;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;
using Xunit;

namespace Yatagarasu.Tests;

[Collection("binary-backend-maintenance")]
public sealed class PropertyVacuumOwnerTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string kind in new[] { "vertex", "edge", "nexus" })
        foreach (bool deleted in new[] { false, true })
        foreach (bool heldReader in new[] { false, true })
        foreach (string target in new[] { "properties", "entity", "all" })
        foreach (string value in new[] { "scalar", "spillover", "vector" })
            yield return [kind, deleted, heldReader, target, value];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Vacuum_respects_owner_horizon_targets_and_reopen(string kind, bool deleted, bool heldReader, string target, string value)
    {
        string directory = Path.Combine(Path.GetTempPath(), "yatagarasu_property_vacuum_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "graph.yata");
        try
        {
            EntityRef owner;
            VertexId replacement;
            using (var db = YatagarasuDatabase.Open(path))
            {
                using (var tx = db.BeginWriteTransaction())
                {
                    var a = tx.CreateVertex("A");
                    var b = tx.CreateVertex("B");
                    owner = kind switch
                    {
                        "vertex" => EntityRef.From(tx.CreateVertex("Owner")),
                        "edge" => EntityRef.From(tx.CreateEdge(a, b, "Link")),
                        _ => EntityRef.From(tx.CreateNexus("Fact", [new("a", a), new("b", b)])),
                    };
                    Set(tx, owner, Value(value, 1));
                    tx.Commit();
                }
                using (var reader = heldReader ? db.BeginReadTransaction() : null)
                {
                    using (var tx = db.BeginWriteTransaction())
                    {
                        if (deleted) Delete(tx, owner); else Set(tx, owner, Value(value, 2));
                        tx.Commit();
                    }
                    var options = new VacuumOptions { Targets = Target(kind, target) };
                    int expected = deleted || target != "entity" ? 1 : 0;
                    var dry = db.Vacuum(new VacuumOptions { Targets = options.Targets, Mode = VacuumMode.DryRun });
                    dry.ReclaimedProperties.Should().Be(heldReader ? 0 : expected);
                    db.Vacuum(new VacuumOptions { Targets = options.Targets, Mode = VacuumMode.DryRun })
                        .Should().BeEquivalentTo(dry, config => config.Excluding(x => x.ElapsedMs));
                    var report = db.Vacuum(options);
                    dry.Should().BeEquivalentTo(report, config => config.Excluding(x => x.ElapsedMs));
                    report.ReclaimedProperties.Should().Be(heldReader ? 0 : expected);
                    if (reader is not null) AssertValue(Get(reader, owner), value, 1);
                    AssertNoOrphanVectors(db);
                }
                // 一度回収したheadが後のfree-listや再利用slotを指してはならない。
                db.Vacuum(new VacuumOptions { Targets = Target(kind, target) }).ReclaimedProperties
                    .Should().Be(heldReader && (deleted || target != "entity") ? 1 : 0);
                using (var tx = db.BeginWriteTransaction())
                {
                    replacement = tx.CreateVertex("Replacement");
                    tx.SetProperty(replacement, "value", Value(value, 3));
                    tx.Commit();
                }
                db.Vacuum().ReclaimedProperties.Should().Be(!deleted && target == "entity" ? 1 : 0);
                db.Vacuum().ReclaimedProperties.Should().Be(0);
                AssertNoOrphanVectors(db);
            }
            using var reopened = YatagarasuDatabase.Open(path);
            AssertNoOrphanVectors(reopened);
            using var current = reopened.BeginReadTransaction();
            AssertValue(current.GetProperty(replacement, "value"), value, 3);
            if (!deleted) AssertValue(Get(current, owner), value, 2);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("vertex")]
    [InlineData("edge")]
    [InlineData("nexus")]
    public void Aborted_updates_remain_readable_after_vacuum_and_reopen(string kind)
    {
        string directory = Path.Combine(Path.GetTempPath(), "yatagarasu_property_abort_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "graph.yata");
        try
        {
            EntityRef owner;
            using (var db = YatagarasuDatabase.Open(path))
            {
                using (var tx = db.BeginWriteTransaction())
                {
                    var a = tx.CreateVertex("A");
                    var b = tx.CreateVertex("B");
                    owner = kind switch
                    {
                        "vertex" => EntityRef.From(a),
                        "edge" => EntityRef.From(tx.CreateEdge(a, b, "Link")),
                        _ => EntityRef.From(tx.CreateNexus("Fact", [new("a", a), new("b", b)])),
                    };
                    Set(tx, owner, Value("spillover", 1));
                    tx.Commit();
                }
                using (var tx = db.BeginWriteTransaction()) Set(tx, owner, Value("spillover", 2));
                db.Vacuum().ReclaimedProperties.Should().Be(0);
            }
            using var reopened = YatagarasuDatabase.Open(path);
            using var reader = reopened.BeginReadTransaction();
            AssertValue(Get(reader, owner), "spillover", 1);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(4096, false)]
    [InlineData(4096, true)]
    public void Maintenance_failure_restores_vector_property_owner_head_and_free_lists(int dimensions, bool updateHead)
    {
        string directory = Path.Combine(Path.GetTempPath(), "yatagarasu_vector_undo_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "graph.yata");
        float[] elements = Enumerable.Range(0, dimensions).Select(i => (float)i).ToArray();
        try
        {
            VertexId owner;
            using (var db = YatagarasuDatabase.Open(path))
            {
                using (var tx = db.BeginWriteTransaction())
                {
                    owner = tx.CreateVertex("Owner");
                    tx.SetProperty(owner, "value", PropertyValue.FromFloatArray(elements));
                    tx.Commit();
                }
                var manager = BackendField<TransactionManager>(db, "_txManager");
                var properties = BackendField<PropertyVersionStore>(db, "_propStore");
                var vertices = BackendField<VersionedVertexStore>(db, "_vertexStore");
                using (manager.AcquireMutationLease())
                {
                    Action fail = () => manager.ExecuteMaintenanceWrite<int>(() =>
                    {
                        var plan = properties.PlanVacuum(EntityRef.From(owner), PropertyVersionRef.Create(0, 1),
                            true, long.MaxValue, manager.CommittedRegistry);
                        plan.Reclaimed.Count.Should().Be(1);
                        var head = properties.ApplyVacuum(plan);
                        if (updateHead) vertices.UpdateFirstPropertyRefRaw(owner, head);
                        properties.FinishExternalReclaim();
                        throw new IOException("Injected maintenance failure");
                    });
                    fail.Should().Throw<IOException>().WithMessage("Injected maintenance failure");
                }
                manager.IsFaulted.Should().BeFalse();
                using (var reader = db.BeginReadTransaction())
                    reader.GetProperty(owner, "value").FloatArrayValue.ToArray().Should().Equal(elements);
                AssertNoOrphanVectors(db);
                using (var tx = db.BeginWriteTransaction())
                {
                    tx.SetProperty(owner, "value", PropertyValue.FromFloatArray(elements));
                    tx.Commit();
                }
                db.Vacuum().ReclaimedProperties.Should().Be(1);
                db.Vacuum().ReclaimedProperties.Should().Be(0);
                AssertNoOrphanVectors(db);
            }
            using var reopened = YatagarasuDatabase.Open(path);
            using var current = reopened.BeginReadTransaction();
            current.GetProperty(owner, "value").FloatArrayValue.ToArray().Should().Equal(elements);
            AssertNoOrphanVectors(reopened);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static T BackendField<T>(YatagarasuDatabase db, string name) => (T)db.BackendInternal.GetType()
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(db.BackendInternal)!;

    private static void AssertNoOrphanVectors(YatagarasuDatabase db)
    {
        var store = (PropertyVersionStore)db.BackendInternal.GetType()
            .GetField("_propStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(db.BackendInternal)!;
        store.ScanOrphanVectorPayloads().Should().BeEmpty();
    }

    private static VacuumTarget Target(string kind, string target) => target switch
    {
        "properties" => VacuumTarget.Properties,
        "all" => VacuumTarget.All,
        _ => kind switch { "vertex" => VacuumTarget.Vertices, "edge" => VacuumTarget.Edges, _ => VacuumTarget.Nexuses },
    };

    private static PropertyValue Value(string type, int version) => type switch
    {
        "spillover" => PropertyValue.FromString(new string((char)('a' + version), 300)),
        "vector" => PropertyValue.FromFloatArray(new float[] { version, 2, 3 }),
        _ => PropertyValue.FromInt32(version),
    };

    private static void AssertValue(PropertyValue actual, string type, int version)
    {
        if (type == "vector") actual.FloatArrayValue.ToArray().Should().Equal((float)version, 2, 3);
        else if (type == "spillover") System.Text.Encoding.UTF8.GetString(actual.Utf8StringValue).Should().Be(new string((char)('a' + version), 300));
        else actual.Int32Value.Should().Be(version);
    }

    private static void Set(IWriteTransaction tx, EntityRef owner, PropertyValue value)
    {
        switch (owner.Kind)
        {
            case EntityKind.Vertex: tx.SetProperty(new VertexId(owner.Value), "value", value); break;
            case EntityKind.Edge: tx.SetProperty(new EdgeId(owner.Value), "value", value); break;
            default: tx.SetProperty(new NexusId(owner.Value), "value", value); break;
        }
    }

    private static void Delete(IWriteTransaction tx, EntityRef owner)
    {
        switch (owner.Kind)
        {
            case EntityKind.Vertex: tx.DeleteVertex(new VertexId(owner.Value)); break;
            case EntityKind.Edge: tx.DeleteEdge(new EdgeId(owner.Value)); break;
            default: tx.DeleteNexus(new NexusId(owner.Value)); break;
        }
    }

    private static PropertyValue Get(IReadTransaction tx, EntityRef owner) => owner.Kind switch
    {
        EntityKind.Vertex => tx.GetProperty(new VertexId(owner.Value), "value"),
        EntityKind.Edge => tx.GetProperty(new EdgeId(owner.Value), "value"),
        _ => tx.GetProperty(new NexusId(owner.Value), "value"),
    };
}
