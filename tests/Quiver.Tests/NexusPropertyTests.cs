using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// Nexusを vertex / edge と同じプロパティエンティティとして扱えることを検証する。
/// スカラ / 文字列 / バイト列 / float 配列、inline と overflow の相互遷移、Set cardinality、
/// スナップショット分離、再オープン後の永続性をカバーする。
/// </summary>
public sealed class NexusPropertyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public NexusPropertyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_hyp2b_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static NexusId Seed(IGraphTransaction tx)
    {
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        return tx.CreateNexus("Fact", [new("Subject", a), new("Object", b)]);
    }

    // ── プロパティの型 ─────────────────────────────────────

    [Fact]
    public void Scalar_property_roundtrips()
    {
        using var db = QuiverDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var he = Seed(tx);

        tx.SetProperty(he, "count", PropertyValue.FromInt32(7));
        tx.SetProperty(he, "weight", PropertyValue.FromDouble(1.5));
        tx.SetProperty(he, "active", PropertyValue.FromBool(true));

        tx.GetProperty(he, "count").Int32Value.Should().Be(7);
        tx.GetProperty(he, "weight").DoubleValue.Should().Be(1.5);
        tx.GetProperty(he, "active").BoolValue.Should().BeTrue();
    }

    [Fact]
    public void String_property_roundtrips()
    {
        using var db = QuiverDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var he = Seed(tx);

        tx.SetProperty(he, "label", PropertyValue.FromString("bought"));
        Utf8(tx.GetProperty(he, "label")).Should().Be("bought");
    }

    [Fact]
    public void Bytes_property_roundtrips()
    {
        using var db = QuiverDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var he = Seed(tx);

        var payload = new byte[] { 1, 2, 3, 250, 255 };
        tx.SetProperty(he, "blob", PropertyValue.FromBytes(payload));
        tx.GetProperty(he, "blob").BytesValue.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void FloatArray_property_roundtrips()
    {
        using var db = QuiverDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var he = Seed(tx);

        var vec = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };
        tx.SetProperty(he, "embedding", PropertyValue.FromFloatArray(vec));
        tx.GetProperty(he, "embedding").FloatArrayValue.ToArray().Should().Equal(vec);
    }

    [Fact]
    public void HasProperty_and_RemoveProperty()
    {
        using var db = QuiverDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var he = Seed(tx);

        tx.SetProperty(he, "k", PropertyValue.FromInt32(1));
        tx.HasProperty(he, "k").Should().BeTrue();
        tx.HasProperty(he, "missing").Should().BeFalse();

        tx.RemoveProperty(he, "k");
        tx.HasProperty(he, "k").Should().BeFalse();
    }

    [Fact]
    public void EnumerateProperties_returns_all_keys()
    {
        using var db = QuiverDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var he = Seed(tx);

        tx.SetProperty(he, "a", PropertyValue.FromInt32(1));
        tx.SetProperty(he, "b", PropertyValue.FromString("x"));

        var keys = new List<PropertyKeyId>();
        var e = tx.EnumerateProperties(he);
        while (e.MoveNext()) keys.Add(e.Current.KeyId);
        keys.Should().HaveCount(2);
    }

    // ── inline / overflow 遷移 ─────────────────────────────

    [Fact]
    public void Inline_to_overflow_and_back_to_inline()
    {
        using var db = QuiverDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var he = Seed(tx);

        // 小さい値は inline。
        tx.SetProperty(he, "note", PropertyValue.FromString("hi"));
        Utf8(tx.GetProperty(he, "note")).Should().Be("hi");

        // 255 バイト超の文字列は inline に収まらず overflow チェーンへ移る。
        var big = new string('x', 400);
        tx.SetProperty(he, "note", PropertyValue.FromString(big));
        Utf8(tx.GetProperty(he, "note")).Should().Be(big);

        // 再び小さい値にすると inline へ戻り、overflow 側の旧値は残さない。
        tx.SetProperty(he, "note", PropertyValue.FromString("bye"));
        Utf8(tx.GetProperty(he, "note")).Should().Be("bye");

        // 遷移を繰り返しても "note" は 1 件だけで、inline と overflow に二重化しないこと。
        int count = 0;
        var e = tx.EnumerateProperties(he);
        while (e.MoveNext()) count++;
        count.Should().Be(1);
    }

    [Fact]
    public void Overflow_property_survives_reopen()
    {
        var big = new string('z', 512);
        NexusId he;
        using (var db = QuiverDatabase.Open(_path))
        using (var tx = db.BeginTransaction())
        {
            he = Seed(tx);
            tx.SetProperty(he, "big", PropertyValue.FromString(big));
            tx.SetProperty(he, "small", PropertyValue.FromInt64(42));
            tx.Commit();
        }

        // 再オープン (open 時の recovery を経由) してもプロパティが残ること。
        using (var db = QuiverDatabase.Open(_path))
        using (var tx = db.BeginTransaction())
        {
            Utf8(tx.GetProperty(he, "big")).Should().Be(big);
            tx.GetProperty(he, "small").Int64Value.Should().Be(42);
        }
    }

    // ── Set cardinality ───────────────────────────────────

    [Fact]
    public void Set_cardinality_add_get_remove()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var he = Seed(tx);

        tx.AddPropertyValue(he, "tags", PropertyValue.FromString("a"));
        tx.AddPropertyValue(he, "tags", PropertyValue.FromString("b"));
        tx.AddPropertyValue(he, "tags", PropertyValue.FromString("a")); // 重複はスキップ

        CollectStrings(tx.GetPropertyValues(he, "tags")).Should().BeEquivalentTo(["a", "b"]);

        tx.RemovePropertyValue(he, "tags", PropertyValue.FromString("a"));
        CollectStrings(tx.GetPropertyValues(he, "tags")).Should().BeEquivalentTo(["b"]);
    }

    [Fact]
    public void SetProperty_on_set_key_throws()
    {
        using var db = QuiverDatabase.Open(_path);
        db.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);

        using var tx = db.BeginTransaction();
        var he = Seed(tx);

        var act = () => tx.SetProperty(he, "tags", PropertyValue.FromString("x"));
        act.Should().Throw<InvalidOperationException>();
    }

    // ── スナップショット分離 ───────────────────────────────

    [Fact]
    public void Older_snapshot_does_not_see_property_set_after_it()
    {
        using var db = QuiverDatabase.Open(_path);
        NexusId he;
        using (var tx = db.BeginTransaction())
        {
            he = Seed(tx);
            tx.Commit();
        }

        using var reader = db.BeginReadOnlyTransaction();
        reader.HasProperty(he, "k").Should().BeFalse();

        using (var tx = db.BeginTransaction())
        {
            tx.SetProperty(he, "k", PropertyValue.FromInt32(1));
            tx.Commit();
        }

        // 先に開いたリーダーのスナップショットには後から設定した値は見えない。
        reader.HasProperty(he, "k").Should().BeFalse();
    }

    [Fact]
    public void Savepoint_rollback_reverts_property_write()
    {
        using var db = QuiverDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var he = Seed(tx);
        tx.SetProperty(he, "k", PropertyValue.FromInt32(1));

        var sp = tx.Savepoint("before");
        tx.SetProperty(he, "k", PropertyValue.FromInt32(999));
        tx.RollbackTo(sp);

        tx.GetProperty(he, "k").Int32Value.Should().Be(1);
    }

    [Fact]
    public void Property_survives_commit_and_reopen()
    {
        NexusId he;
        using (var db = QuiverDatabase.Open(_path))
        using (var tx = db.BeginTransaction())
        {
            he = Seed(tx);
            tx.SetProperty(he, "name", PropertyValue.FromString("purchase"));
            tx.Commit();
        }

        using (var db = QuiverDatabase.Open(_path))
        using (var tx = db.BeginTransaction())
        {
            Utf8(tx.GetProperty(he, "name")).Should().Be("purchase");
        }
    }

    // ── helpers ───────────────────────────────────────────

    private static string Utf8(in PropertyValue value)
        => System.Text.Encoding.UTF8.GetString(value.Utf8StringValue);

    private static List<string> CollectStrings(PropertyValuesEnumerator e)
    {
        var list = new List<string>();
        while (e.MoveNext())
            list.Add(System.Text.Encoding.UTF8.GetString(e.Current.Utf8StringValue));
        return list;
    }
}
