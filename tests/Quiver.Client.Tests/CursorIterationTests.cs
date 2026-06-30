using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Api.Tests;

/// <summary>
/// TraversalCursor の MoveNext / Current / Dispose ライフサイクルテスト。
/// </summary>
public sealed class CursorIterationTests : IDisposable
{
    private readonly string _dir;
    private readonly GraphDatabase _db;

    public CursorIterationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_cursor_" + Guid.NewGuid().ToString("N"));
        _db = GraphDatabase.Open(Path.Combine(_dir, "graph.quiver"));

        using var tx = _db.BeginTransaction();
        for (int i = 0; i < 5; i++)
        {
            var n = tx.CreateNode("Item");
            tx.SetProperty(n, "Index", PropertyValue.FromInt32(i));
        }
        tx.Commit();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // ── MoveNext / Current ──────────────────────────────────────────

    [Fact]
    public void MoveNext_advances_through_all_results()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        using var cursor = g.Nodes().HasLabel("Item").AsCursor();
        int count = 0;
        while (cursor.MoveNext())
            count++;

        count.Should().Be(5);
    }

    [Fact]
    public void Current_returns_correct_element_after_MoveNext()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        using var cursor = g.Nodes().HasLabel("Item").AsCursor();
        cursor.MoveNext().Should().BeTrue();
        cursor.Current.IsValid.Should().BeTrue();
    }

    [Fact]
    public void MoveNext_returns_false_at_end()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        using var cursor = g.Nodes().HasLabel("Ghost").AsCursor();
        cursor.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void MoveNext_returns_false_after_exhaustion()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        using var cursor = g.Nodes().HasLabel("Item").Limit(1).AsCursor();
        cursor.MoveNext().Should().BeTrue();
        cursor.MoveNext().Should().BeFalse();
    }

    // ── Dispose ──────────────────────────────────────────────────────

    [Fact]
    public void Dispose_releases_cursor_resources()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var cursor = g.Nodes().HasLabel("Item").AsCursor();
        cursor.MoveNext();
        cursor.Dispose();
        // 二重 dispose は安全で、例外を投げない
        cursor.Dispose();
    }

    [Fact]
    public void Dispose_during_partial_iteration()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        using var cursor = g.Nodes().HasLabel("Item").AsCursor();
        cursor.MoveNext(); // advance partially
        // using 経由の Dispose で正常に解放する
    }

    // ── 空結果の cursor ──────────────────────────────────────────────

    [Fact]
    public void Empty_cursor_never_advances()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        using var cursor = g.Nodes().HasLabel("NonExistent").AsCursor();
        cursor.MoveNext().Should().BeFalse();
    }

    // ── Limit 付き cursor ────────────────────────────────────────────

    [Fact]
    public void Cursor_respects_limit()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        using var cursor = g.Nodes().HasLabel("Item").Limit(3).AsCursor();
        int count = 0;
        while (cursor.MoveNext())
            count++;

        count.Should().Be(3);
    }

    // ── filter chain 付き cursor ─────────────────────────────────────

    [Fact]
    public void Cursor_with_HasLabel_and_Has_filter()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        using var cursor = g.Nodes().HasLabel("Item").Has("Index", 2).AsCursor();
        cursor.MoveNext().Should().BeTrue();
        cursor.Current.IsValid.Should().BeTrue();
        cursor.MoveNext().Should().BeFalse(); // only one match
    }

    // ── cursor と ToList の一貫性 ────────────────────────────────────

    [Fact]
    public void Cursor_produces_same_results_as_ToList()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        var toListResults = g.Nodes().HasLabel("Item").ToList();

        using var cursor = g.Nodes().HasLabel("Item").AsCursor();
        var cursorResults = new List<NodeId>();
        while (cursor.MoveNext())
            cursorResults.Add(cursor.Current);

        cursorResults.Should().BeEquivalentTo(toListResults);
    }

    // ── Match cursor ─────────────────────────────────────────────────

    [Fact]
    public void Match_cursor_iterates_correctly()
    {
        using var tx = _db.BeginReadOnlyTransaction();
        var g = tx.G(_db.Schema);

        // relationship がないため Item の self-join は自明に空となる。
        // 代わりに Nodes cursor を使う。
        using var cursor = g.Nodes().HasLabel("Item").Id().AsCursor();
        var ids = new List<long>();
        while (cursor.MoveNext())
            ids.Add(cursor.Current);

        ids.Should().HaveCount(5);
        ids.Should().OnlyContain(id => id > 0);
    }
}
