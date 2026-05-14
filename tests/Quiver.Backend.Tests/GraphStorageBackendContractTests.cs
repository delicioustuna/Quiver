using FluentAssertions;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Backend.Tests;

/// <summary>
/// Backend contract tests (BA-2). Subclass for each backend factory and override
/// <see cref="CreateFactory"/>; the same test surface is exercised against every
/// implementation of <see cref="IGraphStorageBackend"/>.
/// </summary>
public abstract class GraphStorageBackendContractTests : IDisposable
{
    private readonly string _dir;
    private readonly IGraphStorageBackendFactory _factory;
    private IGraphStorageBackend _backend;

    protected GraphStorageBackendContractTests()
    {
        _dir = Path.Combine(
            Path.GetTempPath(),
            "quiver_backend_contract_" + Guid.NewGuid().ToString("N"));
        _factory = CreateFactory();
        _backend = _factory.Open(_dir, new GraphDatabaseOptions());
    }

    public void Dispose()
    {
        _backend.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Factory under test. Each concrete subclass returns its own backend factory
    /// (binary, SQLite, in-memory, etc.).
    /// </summary>
    protected abstract IGraphStorageBackendFactory CreateFactory();

    private IGraphTransaction BeginWrite() =>
        _backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: false);

    private IGraphTransaction BeginRead() =>
        _backend.BeginGraphTransaction(IsolationLevel.SnapshotIsolation, readOnly: true);

    private void Reopen()
    {
        _backend.Dispose();
        _backend = _factory.Open(_dir, new GraphDatabaseOptions());
    }

    // ===== Node CRUD =====

    [Fact]
    public void CreateNode_then_NodeExists_returns_true()
    {
        using var tx = BeginWrite();
        var id = tx.CreateNode("Person");
        tx.NodeExists(id).Should().BeTrue();
        tx.Commit();
    }

    [Fact]
    public void DeleteNode_makes_NodeExists_false()
    {
        using var tx = BeginWrite();
        var id = tx.CreateNode("Person");
        tx.DeleteNode(id);
        tx.NodeExists(id).Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void CreateNode_with_LabelId_resolved_via_Schema_works()
    {
        var labelId = _backend.Schema.GetOrCreateLabel("Asset");
        using var tx = BeginWrite();
        var id = tx.CreateNode(labelId);
        tx.NodeExists(id).Should().BeTrue();
        tx.Commit();
    }

    // ===== Relationship CRUD =====

    [Fact]
    public void CreateRelationship_then_enumerate_finds_neighbor()
    {
        using var tx = BeginWrite();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        tx.CreateRelationship(a, b, "LINK");

        var neighbors = new List<NodeId>();
        var en = tx.EnumerateRelationships(a);
        while (en.MoveNext())
        {
            var r = en.Current;
            neighbors.Add(r.Source == a ? r.Target : r.Source);
        }

        neighbors.Should().ContainSingle().Which.Should().Be(b);
        tx.Commit();
    }

    [Fact]
    public void DeleteRelationship_removes_it_from_enumeration()
    {
        using var tx = BeginWrite();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var rel = tx.CreateRelationship(a, b, "LINK");
        tx.DeleteRelationship(rel);

        var en = tx.EnumerateRelationships(a);
        en.MoveNext().Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void DeleteNode_with_incident_relationship_succeeds()
    {
        using var tx = BeginWrite();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        tx.CreateRelationship(a, b, "LINK");
        tx.DeleteNode(a);
        tx.NodeExists(a).Should().BeFalse();
        tx.Commit();
    }

    // ===== Property CRUD =====

    [Fact]
    public void SetProperty_then_GetProperty_returns_value()
    {
        using var tx = BeginWrite();
        var id = tx.CreateNode("Item");
        tx.SetProperty(id, "score", PropertyValue.FromInt64(42L));
        var v = tx.GetProperty(id, "score");
        v.Type.Should().Be(PropertyValueType.Int64);
        v.Int64Value.Should().Be(42L);
        tx.Commit();
    }

    [Fact]
    public void SetProperty_overwrites_previous_value()
    {
        using var tx = BeginWrite();
        var id = tx.CreateNode("X");
        tx.SetProperty(id, "n", PropertyValue.FromInt64(1L));
        tx.SetProperty(id, "n", PropertyValue.FromInt64(99L));
        tx.GetProperty(id, "n").Int64Value.Should().Be(99L);
        tx.Commit();
    }

    [Fact]
    public void RemoveProperty_then_HasProperty_returns_false()
    {
        using var tx = BeginWrite();
        var id = tx.CreateNode("X");
        tx.SetProperty(id, "age", PropertyValue.FromInt32(30));
        tx.RemoveProperty(id, "age");
        tx.HasProperty(id, "age").Should().BeFalse();
        tx.Commit();
    }

    [Fact]
    public void Relationship_property_round_trips()
    {
        using var tx = BeginWrite();
        var a = tx.CreateNode("A");
        var b = tx.CreateNode("B");
        var rel = tx.CreateRelationship(a, b, "LINK");
        tx.SetProperty(rel, "since", PropertyValue.FromInt64(2020L));

        var v = tx.GetProperty(rel, "since");
        v.Type.Should().Be(PropertyValueType.Int64);
        v.Int64Value.Should().Be(2020L);
        tx.Commit();
    }

    // ===== Index seek =====

    [Fact]
    public void IndexInsert_string_then_SeekIndex_finds_node()
    {
        using var tx = BeginWrite();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        tx.IndexInsert("idx_name", "Alice", alice);
        tx.IndexInsert("idx_name", "Bob",   bob);

        var en = tx.SeekIndex("idx_name", PropertyValue.FromString("Alice"));
        var found = new List<NodeId>();
        while (en.MoveNext()) found.Add(en.Current);
        en.Dispose();

        found.Should().ContainSingle().Which.Should().Be(alice);
        tx.Commit();
    }

    [Fact]
    public void IndexInsert_int64_then_SeekIndex_finds_node()
    {
        using var tx = BeginWrite();
        var n42 = tx.CreateNode("Item");
        var n99 = tx.CreateNode("Item");
        tx.IndexInsert("idx_score", 42L, n42);
        tx.IndexInsert("idx_score", 99L, n99);

        var en = tx.SeekIndex("idx_score", PropertyValue.FromInt64(42L));
        var found = new List<NodeId>();
        while (en.MoveNext()) found.Add(en.Current);
        en.Dispose();

        found.Should().ContainSingle().Which.Should().Be(n42);
        tx.Commit();
    }

    [Fact]
    public void SeekIndex_on_unknown_index_returns_empty()
    {
        using var tx = BeginWrite();
        var en = tx.SeekIndex("no_such_index", PropertyValue.FromInt64(1L));
        en.MoveNext().Should().BeFalse();
        en.Dispose();
        tx.Rollback();
    }

    // ===== 1-hop expansion =====

    [Fact]
    public void OneHop_enumerates_outgoing_and_incoming_edges()
    {
        using var tx = BeginWrite();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var carol = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob,   "KNOWS");
        tx.CreateRelationship(carol, alice, "KNOWS");

        var outgoing = new List<NodeId>();
        var incoming = new List<NodeId>();
        var en = tx.EnumerateRelationships(alice);
        while (en.MoveNext())
        {
            var r = en.Current;
            if (r.Source == alice) outgoing.Add(r.Target);
            else                   incoming.Add(r.Source);
        }

        outgoing.Should().ContainSingle().Which.Should().Be(bob);
        incoming.Should().ContainSingle().Which.Should().Be(carol);
        tx.Commit();
    }

    [Fact]
    public void OneHop_type_filter_with_outgoing_direction_returns_matching_edges_only()
    {
        // Direction is honored by EnumerateRelationships when typeFilter is supplied.
        using var tx = BeginWrite();
        var alice = tx.CreateNode("Person");
        var bob   = tx.CreateNode("Person");
        var dave  = tx.CreateNode("Person");
        tx.CreateRelationship(alice, bob,  "KNOWS");
        tx.CreateRelationship(alice, dave, "WORKS_WITH");

        var hits = new List<NodeId>();
        var en = tx.EnumerateRelationships(alice, Direction.Outgoing, typeFilter: "KNOWS");
        while (en.MoveNext())
            hits.Add(en.Current.Target);

        hits.Should().ContainSingle().Which.Should().Be(bob);
        tx.Commit();
    }

    // ===== Commit / Rollback =====

    [Fact]
    public void Commit_persists_writes_to_a_new_transaction()
    {
        NodeId persisted;
        using (var tx = BeginWrite())
        {
            persisted = tx.CreateNode("Persisted");
            tx.SetProperty(persisted, "marker", PropertyValue.FromInt64(7L));
            tx.Commit();
        }

        using var rtx = BeginRead();
        rtx.NodeExists(persisted).Should().BeTrue();
        rtx.GetProperty(persisted, "marker").Int64Value.Should().Be(7L);
        rtx.Rollback();
    }

    [Fact]
    public void Rollback_transitions_state_to_aborted_and_releases_tx()
    {
        // The current engine does not maintain an undo log; rollback releases
        // locks and ends the tx lifecycle. New transactions can be opened
        // immediately afterwards on the same backend.
        var tx = BeginWrite();
        tx.CreateNode("Discarded");
        tx.Rollback();
        tx.State.Should().Be(TransactionState.Aborted);
        tx.Dispose();

        using var next = BeginWrite();
        next.CreateNode("Subsequent");
        next.Commit();
    }

    [Fact]
    public void Commit_survives_backend_reopen()
    {
        NodeId persisted;
        using (var tx = BeginWrite())
        {
            persisted = tx.CreateNode("Survives");
            tx.SetProperty(persisted, "ok", PropertyValue.FromBool(true));
            tx.Commit();
        }

        Reopen();

        using var rtx = BeginRead();
        rtx.NodeExists(persisted).Should().BeTrue();
        rtx.GetProperty(persisted, "ok").Type.Should().Be(PropertyValueType.Bool);
        rtx.Rollback();
    }
}
