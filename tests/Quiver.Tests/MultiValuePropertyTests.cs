using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class MultiValuePropertyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public MultiValuePropertyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_mv2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── AddPropertyValue + GetPropertyValues ────────────────────────

    [Fact]
    public void AddPropertyValue_multiple_values_returned_by_GetPropertyValues()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateNode("Sensor");
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("outdoor"));
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("v2"));
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("active"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var values = Collect(ro.GetPropertyValues(n, "tags"));
        values.Should().HaveCount(3);
        values.Should().Contain("outdoor");
        values.Should().Contain("v2");
        values.Should().Contain("active");
    }

    [Fact]
    public void Duplicate_add_is_skipped_set_semantics()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateNode("Sensor");
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("outdoor"));
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("outdoor"));
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("v2"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var values = Collect(ro.GetPropertyValues(n, "tags"));
        values.Should().HaveCount(2);
    }

    // ── RemovePropertyValue ────────────────────────────────────────

    [Fact]
    public void RemovePropertyValue_removes_specific_value()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateNode("Sensor");
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("outdoor"));
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("v2"));
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("active"));
        tx.RemovePropertyValue(n, "tags", PropertyValue.FromString("v2"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var values = Collect(ro.GetPropertyValues(n, "tags"));
        values.Should().HaveCount(2);
        values.Should().Contain("outdoor");
        values.Should().Contain("active");
        values.Should().NotContain("v2");
    }

    [Fact]
    public void RemovePropertyValue_nonexistent_is_noop()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateNode("Sensor");
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("outdoor"));
        tx.RemovePropertyValue(n, "tags", PropertyValue.FromString("nonexistent"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var values = Collect(ro.GetPropertyValues(n, "tags"));
        values.Should().ContainSingle().Which.Should().Be("outdoor");
    }

    // ── Cardinality enforcement ────────────────────────────────────

    [Fact]
    public void SetProperty_on_Set_key_throws()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateNode("Sensor");
        var act = () => tx.SetProperty(n, "tags", PropertyValue.FromString("x"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*AddPropertyValue*");
    }

    [Fact]
    public void AddPropertyValue_on_Single_key_throws()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("name", PropertyCardinality.Single);

        using var tx = db.BeginTransaction();
        var n = tx.CreateNode("Sensor");
        var act = () => tx.AddPropertyValue(n, "name", PropertyValue.FromString("x"));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GetProperty_on_Set_key_throws()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n = tx.CreateNode("Sensor");
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("outdoor"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        bool threw = false;
        try { _ = ro.GetProperty(n, "tags"); }
        catch (InvalidOperationException) { threw = true; }
        threw.Should().BeTrue();
    }

    [Fact]
    public void RemovePropertyValue_on_Single_key_throws()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("name", PropertyCardinality.Single);

        using var tx = db.BeginTransaction();
        var n = tx.CreateNode("Sensor");
        tx.SetProperty(n, "name", PropertyValue.FromString("hello"));
        var act = () => tx.RemovePropertyValue(n, "name", PropertyValue.FromString("hello"));
        act.Should().Throw<InvalidOperationException>();
    }

    // ── MVCC snapshot isolation ────────────────────────────────────

    [Fact]
    public void Mvcc_snapshot_sees_pre_add_state()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        NodeId n;
        using (var tx = db.BeginTransaction())
        {
            n = tx.CreateNode("Sensor");
            tx.AddPropertyValue(n, "tags", PropertyValue.FromString("outdoor"));
            tx.Commit();
        }

        // Start read-only snapshot before the second write commits
        using var ro = db.BeginReadOnlyTransaction();

        using (var tx2 = db.BeginTransaction())
        {
            tx2.AddPropertyValue(n, "tags", PropertyValue.FromString("new-tag"));
            tx2.Commit();
        }

        // Snapshot should see only the original value
        var values = Collect(ro.GetPropertyValues(n, "tags"));
        values.Should().ContainSingle().Which.Should().Be("outdoor");
    }

    // ── Relationship multi-value ──────────────────────────────────

    [Fact]
    public void Relationship_AddPropertyValue_and_GetPropertyValues()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("labels", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n1 = tx.CreateNode("A");
        var n2 = tx.CreateNode("B");
        var r = tx.CreateRelationship(n1, n2, "KNOWS");
        tx.AddPropertyValue(r, "labels", PropertyValue.FromString("friend"));
        tx.AddPropertyValue(r, "labels", PropertyValue.FromString("colleague"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var values = Collect(ro.GetPropertyValues(r, "labels"));
        values.Should().HaveCount(2);
        values.Should().Contain("friend");
        values.Should().Contain("colleague");
    }

    [Fact]
    public void Relationship_RemovePropertyValue()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("labels", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var n1 = tx.CreateNode("A");
        var n2 = tx.CreateNode("B");
        var r = tx.CreateRelationship(n1, n2, "KNOWS");
        tx.AddPropertyValue(r, "labels", PropertyValue.FromString("friend"));
        tx.AddPropertyValue(r, "labels", PropertyValue.FromString("colleague"));
        tx.RemovePropertyValue(r, "labels", PropertyValue.FromString("friend"));
        tx.Commit();

        using var ro = db.BeginReadOnlyTransaction();
        var values = Collect(ro.GetPropertyValues(r, "labels"));
        values.Should().ContainSingle().Which.Should().Be("colleague");
    }

    // ── Persistence across reopen ─────────────────────────────────

    [Fact]
    public void Values_persist_across_reopen()
    {
        NodeId n;
        using (var db = GraphDatabase.Open(_path))
        {
            db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);
            using var tx = db.BeginTransaction();
            n = tx.CreateNode("Sensor");
            tx.AddPropertyValue(n, "tags", PropertyValue.FromString("outdoor"));
            tx.AddPropertyValue(n, "tags", PropertyValue.FromString("v2"));
            tx.Commit();
        }

        using (var db = GraphDatabase.Open(_path))
        {
            using var ro = db.BeginReadOnlyTransaction();
            var values = Collect(ro.GetPropertyValues(n, "tags"));
            values.Should().HaveCount(2);
            values.Should().Contain("outdoor");
            values.Should().Contain("v2");
        }
    }

    // ── GetPropertyValues on unknown key returns empty ─────────────

    [Fact]
    public void GetPropertyValues_unknown_key_returns_empty()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var n = tx.CreateNode("Sensor");
        var values = Collect(tx.GetPropertyValues(n, "nonexistent"));
        values.Should().BeEmpty();
    }

    // ── Auto-create Set key via AddPropertyValue ───────────────────

    [Fact]
    public void AddPropertyValue_auto_creates_key_as_Set()
    {
        using var db = GraphDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var n = tx.CreateNode("Sensor");
        // No prior Schema.GetOrCreatePropertyKey call — AddPropertyValue auto-creates
        tx.AddPropertyValue(n, "tags", PropertyValue.FromString("outdoor"));
        tx.Commit();

        db.Schema.GetPropertyKeyCardinality(
            db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set))
            .Should().Be(PropertyCardinality.Set);
    }

    // ── Helper ─────────────────────────────────────────────────────

    private static List<string> Collect(PropertyValuesEnumerator enumerator)
    {
        var result = new List<string>();
        while (enumerator.MoveNext())
            result.Add(System.Text.Encoding.UTF8.GetString(enumerator.Current.Utf8StringValue));
        return result;
    }
}
