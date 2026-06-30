using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 数値用 <see cref="PropertyPredicate"/> を数値以外の
/// <see cref="PropertyValueType"/> を持つノードへ適用したとき、偽を返すことを検証する。
/// スカラーレーンを整数として再解釈し、Double や Bool を誤って一致させる回帰を防ぐ。
/// </summary>
public sealed class PropertyTypeFlagsRuntimeTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public PropertyTypeFlagsRuntimeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_ba8_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
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
        // P.Gt(100) が誤一致しないことを確認する。述語は Double 型を認識し、
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
        // Bool のスカラー値を整数として解釈すると P.Gt(0) が true に誤一致する。
        // 型を先に検査して、この入力を拒否する。
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
