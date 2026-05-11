using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Stores;
using Xunit;

namespace Quiver.Stores.Tests;

public class PropertyStoreTests : IDisposable
{
    private readonly string _propPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly string _blobPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly PagedFile _propPf, _blobPf;
    private readonly PropertyStore _store;

    public PropertyStoreTests()
    {
        _propPf = new PagedFile(_propPath);
        _blobPf = new PagedFile(_blobPath);
        _store = new PropertyStore(_propPf, _blobPf);
    }

    public void Dispose()
    {
        _propPf.Dispose(); _blobPf.Dispose();
        System.IO.File.Delete(_propPath); System.IO.File.Delete(_blobPath);
    }

    [Fact]
    public void Create_and_read_int32()
    {
        var id = _store.Create(new PropertyKeyId(1), PropertyValue.FromInt32(42), PropertyId.Invalid);
        using var h = _store.Read(id);
        h.KeyId.Value.Should().Be(1);
        h.Value.Type.Should().Be(PropertyValueType.Int32);
        h.Value.Int32Value.Should().Be(42);
    }

    [Fact]
    public void Create_and_read_bool()
    {
        var id = _store.Create(new PropertyKeyId(2), PropertyValue.FromBool(true), PropertyId.Invalid);
        using var h = _store.Read(id);
        h.Value.BoolValue.Should().BeTrue();
    }

    [Fact]
    public void Create_and_read_double()
    {
        var id = _store.Create(new PropertyKeyId(3), PropertyValue.FromDouble(3.14), PropertyId.Invalid);
        using var h = _store.Read(id);
        h.Value.DoubleValue.Should().BeApproximately(3.14, 1e-10);
    }

    [Fact]
    public void Create_and_read_short_string_inline()
    {
        var val = PropertyValue.FromString("hello");
        var id = _store.Create(new PropertyKeyId(4), val, PropertyId.Invalid);
        using var h = _store.Read(id);
        h.Value.Type.Should().Be(PropertyValueType.String);
        System.Text.Encoding.UTF8.GetString(h.Value.Utf8StringValue).Should().Be("hello");
    }

    [Fact]
    public void Create_and_read_long_string_spillover()
    {
        string longStr = new string('x', 100);
        var val = PropertyValue.FromString(longStr);
        var id = _store.Create(new PropertyKeyId(5), val, PropertyId.Invalid);
        using var h = _store.Read(id);
        System.Text.Encoding.UTF8.GetString(h.Value.Utf8StringValue).Should().Be(longStr);
    }

    [Fact]
    public void Chain_links_correctly()
    {
        var head = PropertyId.Invalid;
        head = _store.Create(new PropertyKeyId(1), PropertyValue.FromInt32(1), head);
        head = _store.Create(new PropertyKeyId(2), PropertyValue.FromInt32(2), head);
        head = _store.Create(new PropertyKeyId(3), PropertyValue.FromInt32(3), head);

        var keys = new List<int>();
        var en = _store.Enumerate(head);
        while (en.MoveNext()) keys.Add(en.Current.KeyId.Value);
        keys.Should().HaveCount(3);
    }

    [Fact]
    public void Delete_head_updates_chain()
    {
        var head = PropertyId.Invalid;
        var first = _store.Create(new PropertyKeyId(1), PropertyValue.FromInt32(1), head);
        var second = _store.Create(new PropertyKeyId(2), PropertyValue.FromInt32(2), first);
        // chain is: second → first → Invalid
        var newHead = _store.Delete(second, second);
        newHead.Should().Be(first);
    }
}
