using FluentAssertions;
using Yatagarasu.Core;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class PropertyCardinalityTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public PropertyCardinalityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yatagarasu_mv1_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.yata");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void GetOrCreate_with_Set_returns_Set_cardinality()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var id = db.EditSchema(schema => schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));
        db.Schema.GetPropertyKeyCardinality(id).Should().Be(PropertyCardinality.Set);
    }

    [Fact]
    public void Default_cardinality_is_Single()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var id = db.EditSchema(schema => schema.GetOrCreatePropertyKey("name"));
        db.Schema.GetPropertyKeyCardinality(id).Should().Be(PropertyCardinality.Single);
    }

    [Fact]
    public void Same_key_same_cardinality_is_idempotent()
    {
        using var db = YatagarasuDatabase.Open(_path);
        var id1 = db.EditSchema(schema => schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));
        var id2 = db.EditSchema(schema => schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));
        id2.Should().Be(id1);
    }

    [Fact]
    public void Same_key_different_cardinality_throws()
    {
        using var db = YatagarasuDatabase.Open(_path);
        db.EditSchema(schema => schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));
        var act = () => db.EditSchema(schema => schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Single));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Existing_Single_key_rejects_Set()
    {
        using var db = YatagarasuDatabase.Open(_path);
        db.EditSchema(schema => schema.GetOrCreatePropertyKey("name"));
        var act = () => db.EditSchema(schema => schema.GetOrCreatePropertyKey("name", PropertyCardinality.Set));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cardinality_persists_across_reopen()
    {
        PropertyKeyId tagsId, nameId;
        using (var db = YatagarasuDatabase.Open(_path))
        {
            tagsId = db.EditSchema(schema => schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));
            nameId = db.EditSchema(schema => schema.GetOrCreatePropertyKey("name", PropertyCardinality.Single));
        }

        using (var db = YatagarasuDatabase.Open(_path))
        {
            db.Schema.GetPropertyKeyCardinality(tagsId).Should().Be(PropertyCardinality.Set);
            db.Schema.GetPropertyKeyCardinality(nameId).Should().Be(PropertyCardinality.Single);

            db.EditSchema(schema => schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set).Should().Be(tagsId));
        }
    }

    [Fact]
    public void Unregistered_key_returns_Single()
    {
        using var db = YatagarasuDatabase.Open(_path);
        db.Schema.GetPropertyKeyCardinality(new PropertyKeyId(999)).Should().Be(PropertyCardinality.Single);
    }
}
