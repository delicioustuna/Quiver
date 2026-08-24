using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Storage.Records.Tests;

public class PropertyStoreTests : IDisposable
{
    private static readonly EntityRef Owner = EntityRef.From(VertexId.Create(7, 1));
    private readonly string _propPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly string _blobPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly string _vectorMetadataPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly string _vectorBlobPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly PagedFile _propPf, _blobPf, _vectorMetadataPf, _vectorBlobPf;
    private readonly PropertyVersionStore _store;

    public PropertyStoreTests()
    {
        _propPf = new PagedFile(_propPath);
        _blobPf = new PagedFile(_blobPath);
        _vectorMetadataPf = new PagedFile(_vectorMetadataPath);
        _vectorBlobPf = new PagedFile(_vectorBlobPath);
        _store = new PropertyVersionStore(_propPf, _blobPf, _vectorMetadataPf, _vectorBlobPf);
    }

    public void Dispose()
    {
        _propPf.Dispose(); _blobPf.Dispose(); _vectorMetadataPf.Dispose(); _vectorBlobPf.Dispose();
        System.IO.File.Delete(_propPath); System.IO.File.Delete(_blobPath);
        System.IO.File.Delete(_vectorMetadataPath); System.IO.File.Delete(_vectorBlobPath);
    }

    [Fact]
    public void Create_and_read_int32()
    {
        var id = Create(1, PropertyValue.FromInt32(42));
        var h = _store.Read(Owner, id);
        h.Address.Key.Value.Should().Be(1);
        h.Value.Type.Should().Be(PropertyValueType.Int32);
        h.Value.Int32Value.Should().Be(42);
    }

    [Fact]
    public void Create_and_read_bool()
    {
        var id = Create(2, PropertyValue.FromBool(true));
        var h = _store.Read(Owner, id);
        h.Value.BoolValue.Should().BeTrue();
    }

    [Fact]
    public void Create_and_read_double()
    {
        var id = Create(3, PropertyValue.FromDouble(3.14));
        var h = _store.Read(Owner, id);
        h.Value.DoubleValue.Should().BeApproximately(3.14, 1e-10);
    }

    [Fact]
    public void Create_and_read_short_string_inline()
    {
        var val = PropertyValue.FromString("hello");
        var id = Create(4, val);
        var h = _store.Read(Owner, id);
        h.Value.Type.Should().Be(PropertyValueType.String);
        System.Text.Encoding.UTF8.GetString(h.Value.Utf8StringValue).Should().Be("hello");
    }

    [Fact]
    public void Create_and_read_long_string_spillover()
    {
        string longStr = new string('x', 100);
        var val = PropertyValue.FromString(longStr);
        var id = Create(5, val);
        var h = _store.Read(Owner, id);
        System.Text.Encoding.UTF8.GetString(h.Value.Utf8StringValue).Should().Be(longStr);
    }

    [Fact]
    public void Chain_links_correctly()
    {
        var head = PropertyVersionRef.Invalid;
        head = Create(1, PropertyValue.FromInt32(1), head);
        head = Create(2, PropertyValue.FromInt32(2), head);
        head = Create(3, PropertyValue.FromInt32(3), head);

        var keys = new List<int>();
        var en = _store.Enumerate(Owner, head);
        while (en.MoveNext()) keys.Add(en.Current.KeyId.Value);
        keys.Should().HaveCount(3);
    }

    [Fact]
    public void Delete_returns_unchanged_head_and_skips_invisible_in_enumerate()
    {
        // MVCC では Delete はチェーンを unlink せず xmax をスタンプするだけ。
        // 戻り値は currentFirst のまま (snapshot reader が辿れるよう head 維持)。
        // 新規 reader からは Enumerate が invisible (= xmax committed) を skip するので
        // first だけが見える。
        var head = PropertyVersionRef.Invalid;
        var first = Create(1, PropertyValue.FromInt32(1), head);
        var second = Create(2, PropertyValue.FromInt32(2), first);
        var newHead = _store.Delete(Owner, second, second);
        newHead.Should().Be(second, "MVCC では論理削除のみで物理 chain は不変");

        var keys = new List<int>();
        var en = _store.Enumerate(Owner, newHead);
        while (en.MoveNext()) keys.Add(en.Current.KeyId.Value);
        keys.Should().BeEquivalentTo(new[] { 1 }, "削除した key=2 は visibility で skip される");
    }

    [Fact]
    public void Create_and_read_FloatArray_inline()
    {
        float[] data = [1.0f, 2.0f, 3.0f];
        var val = PropertyValue.FromFloatArray(data);
        var id = Create(10, val);
        var h = _store.Read(Owner, id);
        h.Value.Type.Should().Be(PropertyValueType.FloatArray);
        h.Value.FloatArrayValue.ToArray().Should().Equal(data);
    }

    [Fact]
    public void Create_and_read_FloatArray_spillover()
    {
        var data = new float[256];
        for (int i = 0; i < data.Length; i++) data[i] = i * 0.5f;
        var val = PropertyValue.FromFloatArray(data);
        var id = Create(11, val);
        var h = _store.Read(Owner, id);
        h.Value.Type.Should().Be(PropertyValueType.FloatArray);
        h.Value.FloatArrayValue.ToArray().Should().Equal(data);
    }

    private PropertyVersionRef Create(
        int key,
        PropertyValue value,
        PropertyVersionRef currentFirst = default)
        => _store.Create(
            new PropertyAddress(Owner, new PropertyKeyId(key)),
            PropertyCardinality.Single,
            in value,
            currentFirst.IsValid ? currentFirst : PropertyVersionRef.Invalid);
}
