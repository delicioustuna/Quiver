using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Query.Physical;
using Yatagarasu.Storage.Records;
using Yatagarasu.Query.Physical.Tests.Support;
using Xunit;

namespace Yatagarasu.Query.Physical.Tests;

public class VertexIndexRangeScanOperatorTests
{
    [Fact]
    public void Empty_index_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_v", new PropertyTarget(PropertyOwnerKind.Vertex, "v", "Item"), IndexKind.Int64Equality)));
        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(Range(
            tx, "idx_v", LiteralProvider.Int64(0), true, LiteralProvider.Int64(100), true));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Range_returns_in_window()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_v", new PropertyTarget(PropertyOwnerKind.Vertex, "v", "Item"), IndexKind.Int64Equality)));
        using (var tx = fx.Db.BeginWriteTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var n = tx.CreateVertex("Item");
                tx.SetProperty(n, "v", PropertyValue.FromInt64(i));
                tx.SetIndexedProperty("idx_v", (long)i, n);
            }
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(Range(
            tx2, "idx_v", LiteralProvider.Int64(1), true, LiteralProvider.Int64(3), true));
        result.Rows().Should().HaveCount(3); // 1, 2, 3
        tx2.Rollback();
    }

    [Fact]
    public void Exclusive_bounds_drop_endpoints()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_v", new PropertyTarget(PropertyOwnerKind.Vertex, "v", "Item"), IndexKind.Int64Equality)));
        using (var tx = fx.Db.BeginWriteTransaction())
        {
            for (int i = 1; i <= 3; i++)
            {
                var n = tx.CreateVertex("Item");
                tx.SetProperty(n, "v", PropertyValue.FromInt64(i));
                tx.SetIndexedProperty("idx_v", (long)i, n);
            }
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(Range(
            tx2, "idx_v", LiteralProvider.Int64(1), false, LiteralProvider.Int64(3), false));
        result.Rows().Should().HaveCount(1); // only 2
        tx2.Rollback();
    }

    [Fact]
    public void Double_range_orders_across_negative_boundary()
    {
        // 浮動小数点インデックスの範囲シークが負値をまたいでも正しく並ぶことを確認する。
        // DoubleKeyCodec の total-order 変換 (符号ビット反転 + 負値は全ビット反転) を検証する。
        // 素朴な DoubleToInt64Bits 直格納では負値が逆順になりこのテストは落ちる。
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_d", new PropertyTarget(PropertyOwnerKind.Vertex, "d", "Item"), IndexKind.DoubleEquality)));
        double[] vals = { -2.5, -0.5, 0.0, 1.5, 3.0 };
        using (var tx = fx.Db.BeginWriteTransaction())
        {
            foreach (var v in vals)
            {
                var n = tx.CreateVertex("Item");
                tx.SetProperty(n, "d", PropertyValue.FromDouble(v));
                tx.SetIndexedProperty("idx_d", v, n);
            }
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginWriteTransaction();
        // [-1.0, 2.0] は符号境界をまたいで -0.5 / 0.0 / 1.5 を含む。
        using var result = tx2.Execute(Range(
            tx2, "idx_d", LiteralProvider.Double(-1.0), true, LiteralProvider.Double(2.0), true));
        result.Rows().Should().HaveCount(3);
        tx2.Rollback();
    }

    [Fact]
    public void Empty_window_yields_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty();
        fx.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_v", new PropertyTarget(PropertyOwnerKind.Vertex, "v", "Item"), IndexKind.Int64Equality)));
        using (var tx = fx.Db.BeginWriteTransaction())
        {
            var n = tx.CreateVertex("Item");
            tx.SetProperty(n, "v", PropertyValue.FromInt64(5));
            tx.SetIndexedProperty("idx_v", 5L, n);
            tx.Commit();
        }
        using var tx2 = fx.Db.BeginWriteTransaction();
        using var result = tx2.Execute(Range(
            tx2, "idx_v", LiteralProvider.Int64(10), true, LiteralProvider.Int64(20), true));
        result.Rows().Should().BeEmpty();
        tx2.Rollback();
    }

    private static VertexIndexRangeScanOperator Range(
        IReadTransaction transaction,
        string indexName,
        ITupleProvider from,
        bool fromInclusive,
        ITupleProvider to,
        bool toInclusive)
    {
        ScalarIndexDefinition definition = transaction.Schema.ListIndexes()
            .Single(index => index.Name == indexName)
            .Definition as ScalarIndexDefinition
            ?? throw new InvalidOperationException($"{indexName} is not a scalar index.");
        if (!transaction.Schema.TryGetPropertyKeyId(
            definition.Target.PropertyKey,
            out PropertyKeyId propertyKey))
            propertyKey = PropertyKeyId.Invalid;
        LabelId? scope = null;
        if (definition.Target.Scope is { } label)
        {
            if (!transaction.Schema.TryGetLabelId(label, out LabelId labelId))
                labelId = LabelId.Invalid;
            scope = labelId;
        }
        return new VertexIndexRangeScanOperator(
            definition,
            propertyKey,
            scope,
            from,
            fromInclusive,
            to,
            toInclusive);
    }
}
