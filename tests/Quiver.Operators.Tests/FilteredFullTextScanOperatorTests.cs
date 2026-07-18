using FluentAssertions;
using Quiver;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Query.Physical.Tests.Support;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Query.Physical.Tests;

/// <summary>
/// <see cref="FilteredFullTextScanOperator"/> を単体で検証する。
/// 入力側の候補集合で採点対象を絞る graph-first の BM25 経路を確認する。
/// </summary>
public sealed class FilteredFullTextScanOperatorTests
{
    private const string IndexName = "idx_body";

    [Fact]
    public void Constructor_rejects_empty_index_name()
    {
        Action act = () => new FilteredFullTextScanOperator(
            new FixedVertexListOperator(), 0, "", "query", k: 5);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rejects_null_query()
    {
        Action act = () => new FilteredFullTextScanOperator(
            new FixedVertexListOperator(), 0, "idx", null!, k: 5);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_rejects_non_positive_k()
    {
        Action act = () => new FilteredFullTextScanOperator(
            new FixedVertexListOperator(), 0, "idx", "q", k: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_rejects_null_source()
    {
        Action act = () => new FilteredFullTextScanOperator(
            null!, 0, "idx", "query", k: 5);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Schema_has_single_VertexId_column()
    {
        var op = new FilteredFullTextScanOperator(
            new FixedVertexListOperator(), 0, IndexName, "hello", k: 3);
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.VertexId);
        op.Dispose();
    }

    [Fact]
    public void Open_on_missing_index_throws_ConstraintException()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ffts_missing");
        using var tx = fx.Db.BeginWriteTransaction();
        var op = new FilteredFullTextScanOperator(
            new FixedVertexListOperator(), 0, "no_such_index", "hello", k: 3);

        Action act = () => op.Open(tx.AsInternal().Inner);
        act.Should().Throw<ConstraintException>();
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Empty_upstream_returns_no_results()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ffts_empty_up");
        fx.EditSchema(schema => schema.CreateFullTextIndex(IndexName, "Doc", "body"));
        using (var seed = fx.Db.BeginWriteTransaction())
        {
            var n = seed.CreateVertex("Doc");
            seed.SetProperty(n, "body", PropertyValue.FromString("hello world"));
            seed.Commit();
        }

        // 入力側が候補を 1 件も生成しない場合。
        using var tx = fx.Db.BeginWriteTransaction();
        var source = new FixedVertexListOperator(); // no vertices
        var op = new FilteredFullTextScanOperator(source, 0, IndexName, "hello", k: 10);
        op.Open(tx.AsInternal().Inner);
        op.MoveNext().Should().BeFalse();
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Filter_excludes_non_candidate_vertices()
    {
        VertexId included = default;
        VertexId excluded = default;
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ffts_filter");
        fx.EditSchema(schema => schema.CreateFullTextIndex(IndexName, "Doc", "body"));
        using (var seed = fx.Db.BeginWriteTransaction())
        {
            included = seed.CreateVertex("Doc");
            seed.SetProperty(included, "body", PropertyValue.FromString("hello world"));
            excluded = seed.CreateVertex("Doc");
            seed.SetProperty(excluded, "body", PropertyValue.FromString("hello universe"));
            seed.Commit();
        }

        var label = fx.EditSchema(schema => schema.GetOrCreateLabel("Doc"));
        using var tx = fx.Db.BeginWriteTransaction();
        var source = new VertexByLabelScanOperator(label);
        var op = new FilteredFullTextScanOperator(source, 0, IndexName, "hello", k: 10);
        op.Open(tx.AsInternal().Inner);
        var count = 0;
        while (op.MoveNext()) count++;
        count.Should().Be(2);
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void No_hit_among_candidates_returns_empty()
    {
        VertexId candidate = default;
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ffts_nohit");
        fx.EditSchema(schema => schema.CreateFullTextIndex(IndexName, "Doc", "body"));
        using (var seed = fx.Db.BeginWriteTransaction())
        {
            candidate = seed.CreateVertex("Doc");
            seed.SetProperty(candidate, "body", PropertyValue.FromString("alpha beta"));
            seed.Commit();
        }

        using var tx = fx.Db.BeginWriteTransaction();
        var source = new FixedVertexListOperator(candidate);
        using var result = tx.Execute(
            new FilteredFullTextScanOperator(source, 0, IndexName, "zzzzz", k: 10));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void K_limits_filtered_results()
    {
        var ids = new VertexId[5];
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ffts_klimit");
        fx.EditSchema(schema => schema.CreateFullTextIndex(IndexName, "Doc", "body"));
        using (var seed = fx.Db.BeginWriteTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                ids[i] = seed.CreateVertex("Doc");
                seed.SetProperty(ids[i], "body", PropertyValue.FromString("common term"));
            }
            seed.Commit();
        }

        var label = fx.EditSchema(schema => schema.GetOrCreateLabel("Doc"));
        using var tx = fx.Db.BeginWriteTransaction();
        var source = new VertexByLabelScanOperator(label);
        var op = new FilteredFullTextScanOperator(source, 0, IndexName, "common", k: 2);
        op.Open(tx.AsInternal().Inner);
        var count = 0;
        while (op.MoveNext()) count++;
        count.Should().Be(2);
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Statistics_tracks_rows_produced()
    {
        var ids = new VertexId[3];
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ffts_stats");
        fx.EditSchema(schema => schema.CreateFullTextIndex(IndexName, "Doc", "body"));
        using (var seed = fx.Db.BeginWriteTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                ids[i] = seed.CreateVertex("Doc");
                seed.SetProperty(ids[i], "body", PropertyValue.FromString("target word"));
            }
            seed.Commit();
        }

        using var tx = fx.Db.BeginWriteTransaction();
        var source = new FixedVertexListOperator(ids);
        var op = new FilteredFullTextScanOperator(source, 0, IndexName, "target", k: 10);
        op.Open(tx.AsInternal().Inner);
        var count = 0;
        while (op.MoveNext()) count++;
        op.Statistics.RowsProduced.Should().Be(count);
        op.Dispose();
        tx.Rollback();
    }
}
