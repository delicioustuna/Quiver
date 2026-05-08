using FluentAssertions;
using GraphDb.Engine.Core;
using GraphDb.Engine.Stores;
using Xunit;

namespace GraphDb.Engine.Stores.Tests;

public class TokenStoreTests : IDisposable
{
    private readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

    public void Dispose() => System.IO.File.Delete(_path);

    [Fact]
    public void GetOrCreate_returns_stable_id()
    {
        using var store = new LabelTokenStore(_path);
        var id1 = store.GetOrCreate("Person");
        var id2 = store.GetOrCreate("Person");
        id1.Should().Be(id2);
    }

    [Fact]
    public void Different_names_get_different_ids()
    {
        using var store = new LabelTokenStore(_path);
        var a = store.GetOrCreate("A");
        var b = store.GetOrCreate("B");
        a.Should().NotBe(b);
    }

    [Fact]
    public void TryGet_returns_false_for_unknown()
    {
        using var store = new LabelTokenStore(_path);
        store.TryGet("Unknown", out _).Should().BeFalse();
    }

    [Fact]
    public void GetName_round_trips()
    {
        using var store = new LabelTokenStore(_path);
        var id = store.GetOrCreate("Movie");
        store.GetName(id).Should().Be("Movie");
    }

    [Fact]
    public void GetNameUtf8_round_trips()
    {
        using var store = new LabelTokenStore(_path);
        var id = store.GetOrCreate("Song");
        var bytes = store.GetNameUtf8(id);
        System.Text.Encoding.UTF8.GetString(bytes).Should().Be("Song");
    }

    [Fact]
    public void Persists_across_reload()
    {
        LabelId id;
        {
            using var store = new LabelTokenStore(_path);
            id = store.GetOrCreate("Persisted");
        }
        using var store2 = new LabelTokenStore(_path);
        store2.TryGet("Persisted", out var id2).Should().BeTrue();
        id2.Should().Be(id);
    }

    [Fact]
    public void All_returns_all_tokens()
    {
        using var store = new LabelTokenStore(_path);
        store.GetOrCreate("A");
        store.GetOrCreate("B");
        store.GetOrCreate("C");
        store.All().Should().HaveCount(3);
    }
}
