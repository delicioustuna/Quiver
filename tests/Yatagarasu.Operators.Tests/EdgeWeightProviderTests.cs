using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Query.Physical.Tests.Support;
using Yatagarasu.Storage.Records;
using Xunit;

namespace Yatagarasu.Query.Physical.Tests;

/// <summary>
/// <see cref="PropertyChainWeightProvider"/> および <see cref="PayloadLaneWeightProvider"/>
/// の単体テスト。重みの読み出し、型変換、デフォルト値、payload lane 経路を個別に検証する。
/// </summary>
public sealed class EdgeWeightProviderTests
{
    // ── PropertyChainWeightProvider: ToDouble 静的ヘルパ ─────────────

    [Fact]
    public void ToDouble_converts_double_property()
    {
        var v = PropertyValue.FromDouble(3.14);
        PropertyChainWeightProvider.ToDouble(v).Should().BeApproximately(3.14, 1e-10);
    }

    [Fact]
    public void ToDouble_converts_int64_property()
    {
        var v = PropertyValue.FromInt64(42);
        PropertyChainWeightProvider.ToDouble(v).Should().Be(42.0);
    }

    [Fact]
    public void ToDouble_converts_int32_property()
    {
        var v = PropertyValue.FromInt32(7);
        PropertyChainWeightProvider.ToDouble(v).Should().Be(7.0);
    }

    [Fact]
    public void ToDouble_converts_bool_true_to_1()
    {
        var v = PropertyValue.FromBool(true);
        PropertyChainWeightProvider.ToDouble(v).Should().Be(1.0);
    }

    [Fact]
    public void ToDouble_converts_bool_false_to_0()
    {
        var v = PropertyValue.FromBool(false);
        PropertyChainWeightProvider.ToDouble(v).Should().Be(0.0);
    }

    [Fact]
    public void ToDouble_throws_on_string_property()
    {
        var v = PropertyValue.FromString("hello");
        try
        {
            PropertyChainWeightProvider.ToDouble(v);
            Assert.Fail("Expected InvalidOperationException");
        }
        catch (InvalidOperationException) { }
    }

    // ── PropertyChainWeightProvider: GetWeight 統合 ──────────────────

    [Fact]
    public void GetWeight_returns_property_value_as_weight()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ewp_prop");
        var keyId = fx.EditSchema(schema => schema.GetOrCreatePropertyKey("cost"));
        var provider = new PropertyChainWeightProvider(keyId);

        using var tx = fx.Db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var edge = tx.CreateEdge(a, b, "ROAD");
        tx.SetProperty(edge, "cost", PropertyValue.FromDouble(2.5));
        tx.Commit();

        using var rtx = fx.Db.BeginWriteTransaction();
        var weight = provider.GetWeight(rtx.AsInternal().Inner, edge, 0);
        weight.Should().BeApproximately(2.5, 1e-10);
        rtx.Rollback();
    }

    [Fact]
    public void GetWeight_returns_default_when_property_missing()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ewp_default");
        var keyId = fx.EditSchema(schema => schema.GetOrCreatePropertyKey("cost"));
        var provider = new PropertyChainWeightProvider(keyId, defaultWeight: 1.0);

        using var tx = fx.Db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var edge = tx.CreateEdge(a, b, "ROAD");
        tx.Commit();

        using var rtx = fx.Db.BeginWriteTransaction();
        var weight = provider.GetWeight(rtx.AsInternal().Inner, edge, 0);
        weight.Should().Be(1.0);
        rtx.Rollback();
    }

    [Fact]
    public void GetWeight_respects_custom_default_weight()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ewp_custom_default");
        var keyId = fx.EditSchema(schema => schema.GetOrCreatePropertyKey("dist"));
        var provider = new PropertyChainWeightProvider(keyId, defaultWeight: 99.0);

        using var tx = fx.Db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var edge = tx.CreateEdge(a, b, "PATH");
        tx.Commit();

        using var rtx = fx.Db.BeginWriteTransaction();
        var weight = provider.GetWeight(rtx.AsInternal().Inner, edge, 0);
        weight.Should().Be(99.0);
        rtx.Rollback();
    }

    [Fact]
    public void GetWeight_reads_int64_property_as_weight()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ewp_int64");
        var keyId = fx.EditSchema(schema => schema.GetOrCreatePropertyKey("hops"));
        var provider = new PropertyChainWeightProvider(keyId);

        using var tx = fx.Db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var edge = tx.CreateEdge(a, b, "LINK");
        tx.SetProperty(edge, "hops", PropertyValue.FromInt64(5));
        tx.Commit();

        using var rtx = fx.Db.BeginWriteTransaction();
        var weight = provider.GetWeight(rtx.AsInternal().Inner, edge, 0);
        weight.Should().Be(5.0);
        rtx.Rollback();
    }

    [Fact]
    public void GetWeight_picks_correct_key_among_multiple_properties()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ewp_multi_prop");
        var costKey = fx.EditSchema(schema => schema.GetOrCreatePropertyKey("cost"));
        var nameKey = fx.EditSchema(schema => schema.GetOrCreatePropertyKey("name"));
        var provider = new PropertyChainWeightProvider(costKey);

        using var tx = fx.Db.BeginWriteTransaction();
        var a = tx.CreateVertex("A");
        var b = tx.CreateVertex("B");
        var edge = tx.CreateEdge(a, b, "ROAD");
        tx.SetProperty(edge, "name", PropertyValue.FromString("I-95"));
        tx.SetProperty(edge, "cost", PropertyValue.FromDouble(7.77));
        tx.Commit();

        using var rtx = fx.Db.BeginWriteTransaction();
        var weight = provider.GetWeight(rtx.AsInternal().Inner, edge, 0);
        weight.Should().BeApproximately(7.77, 1e-10);
        rtx.Rollback();
    }

    // ── PayloadLaneWeightProvider ───────────────────────────────────

    [Fact]
    public void PayloadLane_converts_long_bits_to_double()
    {
        var expected = 3.14;
        var raw = BitConverter.DoubleToInt64Bits(expected);
        var weight = PayloadLaneWeightProvider.Instance.GetWeight(null!, default, raw);
        weight.Should().BeApproximately(expected, 1e-10);
    }

    [Fact]
    public void PayloadLane_zero_raw_returns_zero()
    {
        var weight = PayloadLaneWeightProvider.Instance.GetWeight(null!, default, 0);
        weight.Should().Be(0.0);
    }

    [Fact]
    public void PayloadLane_negative_weight()
    {
        var expected = -2.5;
        var raw = BitConverter.DoubleToInt64Bits(expected);
        var weight = PayloadLaneWeightProvider.Instance.GetWeight(null!, default, raw);
        weight.Should().BeApproximately(expected, 1e-10);
    }

    [Fact]
    public void PayloadLane_ignores_transaction_and_edge_id()
    {
        var expected = 1.23;
        var raw = BitConverter.DoubleToInt64Bits(expected);
        var weight = PayloadLaneWeightProvider.Instance.GetWeight(null!, new EdgeId(999), raw);
        weight.Should().BeApproximately(expected, 1e-10);
    }

    [Fact]
    public void PayloadLane_singleton_instance_is_shared()
    {
        PayloadLaneWeightProvider.Instance.Should().BeSameAs(PayloadLaneWeightProvider.Instance);
    }
}
