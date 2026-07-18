using System.Runtime.InteropServices;
using FluentAssertions;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class FloatArrayPropertyTests : IDisposable
{
    private readonly string _dir;
    private readonly QuiverDatabase _db;

    public FloatArrayPropertyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fa_" + Guid.NewGuid().ToString("N"));
        _db = QuiverDatabase.Open(Path.Combine(_dir, "graph.quiver"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void PropertyValue_FloatArray_roundtrip()
    {
        float[] data = [1.0f, 2.5f, -3.0f, 0.0f];
        var pv = PropertyValue.FromFloatArray(data);

        pv.Type.Should().Be(PropertyValueType.FloatArray);
        pv.FloatArrayValue.ToArray().Should().Equal(data);
        pv.EncodedSize.Should().Be(data.Length * sizeof(float));
    }

    [Fact]
    public void PropertyValue_empty_FloatArray()
    {
        var pv = PropertyValue.FromFloatArray(ReadOnlySpan<float>.Empty);

        pv.Type.Should().Be(PropertyValueType.FloatArray);
        pv.FloatArrayValue.Length.Should().Be(0);
        pv.EncodedSize.Should().Be(0);
    }

    [Fact]
    public void PropertyValueType_FloatArray_ToFlags()
    {
        PropertyValueType.FloatArray.ToFlags().Should().Be(PropertyTypeFlags.FloatArray);
    }

    [Fact]
    public void SetProperty_and_GetProperty_FloatArray_inline()
    {
        float[] small = [1.0f, 2.0f, 3.0f];

        using var tx = _db.BeginWriteTransaction();
        var n = tx.CreateVertex("Sensor");
        tx.SetProperty(n, "wave", PropertyValue.FromFloatArray(small));
        tx.Commit();

        using var read = _db.BeginWriteTransaction();
        var pv = read.GetProperty(n, "wave");
        pv.Type.Should().Be(PropertyValueType.FloatArray);
        pv.FloatArrayValue.ToArray().Should().Equal(small);
    }

    [Fact]
    public void SetProperty_and_GetProperty_FloatArray_spillover()
    {
        var large = new float[256];
        for (int i = 0; i < large.Length; i++) large[i] = i * 0.1f;

        using var tx = _db.BeginWriteTransaction();
        var n = tx.CreateVertex("Sensor");
        tx.SetProperty(n, "embedding", PropertyValue.FromFloatArray(large));
        tx.Commit();

        using var read = _db.BeginWriteTransaction();
        var pv = read.GetProperty(n, "embedding");
        pv.Type.Should().Be(PropertyValueType.FloatArray);
        pv.FloatArrayValue.ToArray().Should().Equal(large);
    }

    [Fact]
    public void FloatArray_survives_overwrite()
    {
        float[] first = [1.0f, 2.0f];
        float[] second = [10.0f, 20.0f, 30.0f];

        using (var tx = _db.BeginWriteTransaction())
        {
            var n = tx.CreateVertex("Item");
            tx.SetProperty(n, "vec", PropertyValue.FromFloatArray(first));
            tx.Commit();
        }

        VertexId vertexId;
        using (var tx = _db.BeginWriteTransaction())
        {
            vertexId = tx.Query.Vertices().HasLabel("Item").ToList()[0];
            tx.SetProperty(vertexId, "vec", PropertyValue.FromFloatArray(second));
            tx.Commit();
        }

        using var read = _db.BeginWriteTransaction();
        read.GetProperty(vertexId, "vec").FloatArrayValue.ToArray().Should().Equal(second);
    }

    [Fact]
    public void FloatArray_equality()
    {
        float[] a = [1.0f, 2.0f, 3.0f];
        float[] b = [1.0f, 2.0f, 3.0f];
        float[] c = [1.0f, 2.0f, 4.0f];

        var pa = PropertyValue.FromFloatArray(a);
        var pb = PropertyValue.FromFloatArray(b);
        var pc = PropertyValue.FromFloatArray(c);

        PropertyValueEqualityHelper.AreEqual(pa, pb).Should().BeTrue();
        PropertyValueEqualityHelper.AreEqual(pa, pc).Should().BeFalse();
    }

    [Fact]
    public void LogicalPropertyValue_captures_FloatArray()
    {
        float[] data = [1.0f, -2.0f, 3.5f];
        var pv = PropertyValue.FromFloatArray(data);
        var logical = Quiver.Logical.LogicalPropertyValue.Capture(pv);

        logical.Type.Should().Be(PropertyValueType.FloatArray);
        var restored = logical.ToPropertyValue();
        restored.Type.Should().Be(PropertyValueType.FloatArray);
        restored.FloatArrayValue.ToArray().Should().Equal(data);
    }
}
