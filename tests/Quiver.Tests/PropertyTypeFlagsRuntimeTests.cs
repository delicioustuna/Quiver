using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// BA-8 end-to-end: a numeric <see cref="PropertyPredicate"/> applied to a node
/// whose property is stored under a non-numeric <see cref="PropertyValueType"/>
/// must return false. Before BA-8, <c>PropertyInt64Predicate</c> reinterpreted
/// the scalar lane and could lie for <c>Double</c> / <c>Bool</c> values.
/// </summary>
public sealed class PropertyTypeFlagsRuntimeTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public PropertyTypeFlagsRuntimeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_ba8_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(_dir);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Numeric_predicate_rejects_double_typed_property()
    {
        // A "score" stored as Double would previously be reinterpreted as the
        // raw Int64 bit pattern (positive for any non-negative double), so
        // P.Gt(100) would falsely match. With BA-8 the predicate sees a Double
        // type flag and returns false without decoding.
        using var tx = _db.BeginTransaction();
        var n = tx.CreateNode("Item");
        tx.SetProperty(n, "score", PropertyValue.FromDouble(5.0));
        tx.Commit();

        using var read = _db.BeginTransaction();
        var g = read.G(_db.Schema);
        var matches = g.Nodes().HasLabel("Item").Has("score", P.Gt(100L)).ToList();

        matches.Should().BeEmpty();
    }

    [Fact]
    public void Numeric_predicate_rejects_bool_typed_property()
    {
        // Bool stored as 0/1 in the scalar lane. A bare PropertyInt64Predicate
        // pre-BA-8 returned that scalar and so P.Gt(0) on a `true` value
        // erroneously matched. BA-8 rejects the type up front.
        using var tx = _db.BeginTransaction();
        var n = tx.CreateNode("Item");
        tx.SetProperty(n, "active", PropertyValue.FromBool(true));
        tx.Commit();

        using var read = _db.BeginTransaction();
        var g = read.G(_db.Schema);
        var matches = g.Nodes().HasLabel("Item").Has("active", P.Gt(0L)).ToList();

        matches.Should().BeEmpty();
    }

    [Fact]
    public void Numeric_predicate_still_matches_int_typed_property()
    {
        using var tx = _db.BeginTransaction();
        var a = tx.CreateNode("Item");
        var b = tx.CreateNode("Item");
        tx.SetProperty(a, "score", PropertyValue.FromInt32(50));
        tx.SetProperty(b, "score", PropertyValue.FromInt32(150));
        tx.Commit();

        using var read = _db.BeginTransaction();
        var g = read.G(_db.Schema);
        var matches = g.Nodes().HasLabel("Item").Has("score", P.Gt(100L)).ToList();

        matches.Should().ContainSingle().Which.Should().Be(b);
    }
}
