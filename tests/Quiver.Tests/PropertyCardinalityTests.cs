using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Tests;

public sealed class PropertyCardinalityTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public PropertyCardinalityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_mv1_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void GetOrCreate_with_Set_returns_Set_cardinality()
    {
        using var db = GraphDatabase.Open(_path);
        var id = db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);
        db.Schema.GetPropertyKeyCardinality(id).Should().Be(PropertyCardinality.Set);
    }

    [Fact]
    public void Default_cardinality_is_Single()
    {
        using var db = GraphDatabase.Open(_path);
        var id = db.Schema.GetOrCreatePropertyKey("name");
        db.Schema.GetPropertyKeyCardinality(id).Should().Be(PropertyCardinality.Single);
    }

    [Fact]
    public void Same_key_same_cardinality_is_idempotent()
    {
        using var db = GraphDatabase.Open(_path);
        var id1 = db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);
        var id2 = db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);
        id2.Should().Be(id1);
    }

    [Fact]
    public void Same_key_different_cardinality_throws()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);
        var act = () => db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Single);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Existing_Single_key_rejects_Set()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("name");
        var act = () => db.Schema.GetOrCreatePropertyKey("name", PropertyCardinality.Set);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cardinality_persists_across_reopen()
    {
        PropertyKeyId tagsId, nameId;
        using (var db = GraphDatabase.Open(_path))
        {
            tagsId = db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);
            nameId = db.Schema.GetOrCreatePropertyKey("name", PropertyCardinality.Single);
        }

        using (var db = GraphDatabase.Open(_path))
        {
            db.Schema.GetPropertyKeyCardinality(tagsId).Should().Be(PropertyCardinality.Set);
            db.Schema.GetPropertyKeyCardinality(nameId).Should().Be(PropertyCardinality.Single);

            db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set).Should().Be(tagsId);
        }
    }

    [Fact]
    public void Unregistered_key_returns_Single()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.GetPropertyKeyCardinality(new PropertyKeyId(999)).Should().Be(PropertyCardinality.Single);
    }
}
