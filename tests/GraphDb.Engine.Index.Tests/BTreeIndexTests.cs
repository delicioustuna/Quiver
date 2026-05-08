using FluentAssertions;
using GraphDb.Engine.Index;
using GraphDb.Engine.Storage;
using Xunit;

namespace GraphDb.Engine.Index.Tests;

public class BTreeIndexTests : IDisposable
{
    private readonly string _dir;

    public BTreeIndexTests()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(_dir);
    }

    public void Dispose() => System.IO.Directory.Delete(_dir, recursive: true);

    private IBTreeIndex<int> OpenInt32() => new IndexManager(_dir).CreateInt32Index("test");

    [Fact]
    public void Insert_and_seek_single_entry()
    {
        using var idx = OpenInt32();
        idx.Insert(42, 100L);
        idx.EntryCount.Should().Be(1);
        var en = idx.Seek(42);
        en.MoveNext().Should().BeTrue();
        en.Current.Should().Be(100L);
        en.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Insert_multiple_and_seek()
    {
        using var idx = OpenInt32();
        idx.Insert(10, 1); idx.Insert(20, 2); idx.Insert(30, 3);
        var en = idx.Seek(20);
        en.MoveNext().Should().BeTrue();
        en.Current.Should().Be(2);
    }

    [Fact]
    public void Delete_removes_entry()
    {
        using var idx = OpenInt32();
        idx.Insert(5, 50);
        idx.Delete(5, 50).Should().BeTrue();
        idx.EntryCount.Should().Be(0);
        idx.Seek(5).MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Delete_nonexistent_returns_false()
    {
        using var idx = OpenInt32();
        idx.Delete(99, 0).Should().BeFalse();
    }

    [Fact]
    public void Range_scan_returns_in_order()
    {
        using var idx = OpenInt32();
        for (int i = 1; i <= 10; i++) idx.Insert(i, i * 10L);

        var values = new List<long>();
        var en = idx.Range(3, true, 7, true);
        while (en.MoveNext()) values.Add(en.Current.Value);
        values.Should().Equal(30L, 40L, 50L, 60L, 70L);
    }

    [Fact]
    public void Range_exclusive_bounds()
    {
        using var idx = OpenInt32();
        for (int i = 1; i <= 5; i++) idx.Insert(i, i);
        var values = new List<long>();
        var en = idx.Range(1, false, 5, false);
        while (en.MoveNext()) values.Add(en.Current.Value);
        values.Should().Equal(2L, 3L, 4L);
    }

    [Fact]
    public void FullScan_returns_all_in_order()
    {
        using var idx = OpenInt32();
        int[] input = [5, 3, 8, 1, 9, 2, 7, 4, 6, 10];
        foreach (int v in input) idx.Insert(v, v);
        var values = new List<long>();
        var en = idx.FullScan();
        while (en.MoveNext()) values.Add(en.Current.Value);
        values.Should().BeInAscendingOrder();
        values.Should().HaveCount(10);
    }

    [Fact]
    public void Large_insert_causes_splits()
    {
        using var idx = OpenInt32();
        for (int i = 0; i < 2000; i++) idx.Insert(i, i);
        idx.EntryCount.Should().Be(2000);
        idx.Height.Should().BeGreaterThan(1);
        var en = idx.Seek(1000);
        en.MoveNext().Should().BeTrue();
        en.Current.Should().Be(1000L);
    }

    [Fact]
    public void String_index_round_trip()
    {
        using var idx = new IndexManager(_dir).CreateStringIndex("strtest");
        idx.Insert("banana", 2);
        idx.Insert("apple", 1);
        idx.Insert("cherry", 3);
        var values = new List<long>();
        var en = idx.FullScan();
        while (en.MoveNext()) values.Add(en.Current.Value);
        values.Should().Equal(1L, 2L, 3L); // alphabetical order
    }
}
