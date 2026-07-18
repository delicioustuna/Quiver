using FluentAssertions;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Query.Physical.Tests;

/// <summary>
/// <see cref="FullTextScanOperator"/> を単体で検証する。
/// DSL のトラバーサル層を介さず、BM25 全文検索の Bind / Open / MoveNext 契約を確認する。
/// </summary>
public sealed class FullTextScanOperatorTests
{
    private const string IndexName = "idx_body";

    [Fact]
    public void Constructor_rejects_empty_index_name()
    {
        Action act = () => new FullTextScanOperator("", "query", k: 5);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rejects_null_query()
    {
        Action act = () => new FullTextScanOperator("idx", null!, k: 5);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_rejects_non_positive_k()
    {
        Action act = () => new FullTextScanOperator("idx", "q", k: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Schema_has_single_VertexId_column()
    {
        var op = new FullTextScanOperator(IndexName, "hello", k: 3);
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.VertexId);
        op.Dispose();
    }

    [Fact]
    public void Open_on_missing_index_throws_ConstraintException()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "fts_missing");
        using var tx = fx.Db.BeginWriteTransaction();
        var op = new FullTextScanOperator("no_such_index", "hello", k: 3);

        Action act = () => op.Open(tx.AsInternal().Inner);
        act.Should().Throw<ConstraintException>();
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Empty_index_returns_no_results()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "fts_empty");
        fx.EditSchema(schema => schema.CreateFullTextIndex(IndexName, "Doc", "body"));

        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new FullTextScanOperator(IndexName, "hello", k: 10));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void No_hit_query_returns_empty()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "fts_nohit");
        fx.EditSchema(schema => schema.CreateFullTextIndex(IndexName, "Doc", "body"));
        using (var seed = fx.Db.BeginWriteTransaction())
        {
            var n = seed.CreateVertex("Doc");
            seed.SetProperty(n, "body", PropertyValue.FromString("alpha beta gamma"));
            seed.Commit();
        }

        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new FullTextScanOperator(IndexName, "zzzzz", k: 10));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void Single_match_returns_one_row()
    {
        VertexId docId = default;
        using var fx = OperatorTestFixture.OpenEmpty(tag: "fts_single");
        fx.EditSchema(schema => schema.CreateFullTextIndex(IndexName, "Doc", "body"));
        using (var seed = fx.Db.BeginWriteTransaction())
        {
            docId = seed.CreateVertex("Doc");
            seed.SetProperty(docId, "body", PropertyValue.FromString("hello world"));
            seed.Commit();
        }

        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new FullTextScanOperator(IndexName, "hello", k: 10));
        var rows = result.Rows().ToList();
        rows.Should().HaveCount(1);
        tx.Rollback();
    }

    [Fact]
    public void Multiple_hits_ordered_by_relevance()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "fts_rank");
        fx.EditSchema(schema => schema.CreateFullTextIndex(IndexName, "Doc", "body"));
        using (var seed = fx.Db.BeginWriteTransaction())
        {
            var d1 = seed.CreateVertex("Doc");
            seed.SetProperty(d1, "body", PropertyValue.FromString("foo bar"));
            var d2 = seed.CreateVertex("Doc");
            seed.SetProperty(d2, "body", PropertyValue.FromString("foo foo"));
            seed.Commit();
        }

        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new FullTextScanOperator(IndexName, "foo", k: 10));
        var rows = result.Rows().ToList();
        rows.Should().HaveCount(2);
        // 文書長が等しい場合、BM25 は語頻度の高い文書を上位にする。
        tx.Rollback();
    }

    [Fact]
    public void K_limits_result_count()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "fts_klimit");
        fx.EditSchema(schema => schema.CreateFullTextIndex(IndexName, "Doc", "body"));
        using (var seed = fx.Db.BeginWriteTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var n = seed.CreateVertex("Doc");
                seed.SetProperty(n, "body", PropertyValue.FromString("common term here"));
            }
            seed.Commit();
        }

        using var tx = fx.Db.BeginWriteTransaction();
        using var result = tx.Execute(new FullTextScanOperator(IndexName, "common", k: 2));
        result.Rows().Should().HaveCount(2);
        tx.Rollback();
    }

    [Fact]
    public void Statistics_tracks_rows_produced()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "fts_stats");
        fx.EditSchema(schema => schema.CreateFullTextIndex(IndexName, "Doc", "body"));
        using (var seed = fx.Db.BeginWriteTransaction())
        {
            var n1 = seed.CreateVertex("Doc");
            seed.SetProperty(n1, "body", PropertyValue.FromString("alpha"));
            var n2 = seed.CreateVertex("Doc");
            seed.SetProperty(n2, "body", PropertyValue.FromString("alpha beta"));
            seed.Commit();
        }

        using var tx = fx.Db.BeginWriteTransaction();
        var op = new FullTextScanOperator(IndexName, "alpha", k: 10);
        op.Open(tx.AsInternal().Inner);
        var count = 0;
        while (op.MoveNext()) count++;
        op.Statistics.RowsProduced.Should().Be(count);
        op.Dispose();
        tx.Rollback();
    }
}
