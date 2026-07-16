using System.IO;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>ColumnCatalog (列化登録の永続レジストリ) の単体テスト。</summary>
public class ColumnCatalogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private PagedFile _file;
    private ColumnCatalog _cat;

    public ColumnCatalogTests()
    {
        _file = new PagedFile(_path);
        _cat = new ColumnCatalog(_file);
    }

    public void Dispose() { _file.Dispose(); File.Delete(_path); }

    private void Reopen()
    {
        _file.Dispose();
        _file = new PagedFile(_path);
        _cat = new ColumnCatalog(_file);
    }

    [Fact]
    public void Register_assigns_distinct_tenant_ids_from_64()
    {
        var t1 = _cat.Register(EntityKind.Edge, keyId: 1);
        var t2 = _cat.Register(EntityKind.Edge, keyId: 2);
        var t3 = _cat.Register(EntityKind.Vertex, keyId: 1);
        t1.Should().Be(64);
        t2.Should().Be(65);
        t3.Should().Be(66);
        // 同一 (kind,key) の再登録は同じ ID。
        _cat.Register(EntityKind.Edge, keyId: 1).Should().Be(64);
    }

    [Fact]
    public void TryGet_distinguishes_kind_and_key()
    {
        _cat.Register(EntityKind.Edge, 5);
        _cat.TryGet(EntityKind.Edge, 5, out var t).Should().BeTrue();
        t.Should().Be(64);
        _cat.TryGet(EntityKind.Vertex, 5, out _).Should().BeFalse();
        _cat.TryGet(EntityKind.Edge, 6, out _).Should().BeFalse();
    }

    [Fact]
    public void Registration_persists_across_reopen()
    {
        _cat.Register(EntityKind.Edge, 1);
        _cat.Register(EntityKind.Vertex, 7);

        Reopen();

        _cat.Entries.Count.Should().Be(2);
        _cat.TryGet(EntityKind.Edge, 1, out var t1).Should().BeTrue();
        t1.Should().Be(64);
        _cat.TryGet(EntityKind.Vertex, 7, out var t2).Should().BeTrue();
        t2.Should().Be(65);
        // 次の Register は 66 から (counter も永続)。
        _cat.Register(EntityKind.Edge, 9).Should().Be(66);
    }

    [Fact]
    public void Unregister_removes_entry()
    {
        _cat.Register(EntityKind.Edge, 1);
        _cat.Register(EntityKind.Edge, 2);
        _cat.Unregister(EntityKind.Edge, 1).Should().BeTrue();
        _cat.TryGet(EntityKind.Edge, 1, out _).Should().BeFalse();
        _cat.TryGet(EntityKind.Edge, 2, out _).Should().BeTrue();
        _cat.Unregister(EntityKind.Edge, 1).Should().BeFalse(); // 二度目は無し

        Reopen();
        _cat.TryGet(EntityKind.Edge, 1, out _).Should().BeFalse();
        _cat.TryGet(EntityKind.Edge, 2, out _).Should().BeTrue();
    }
}
