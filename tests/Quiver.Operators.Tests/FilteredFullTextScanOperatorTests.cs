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
            new FixedNodeListOperator(), 0, "", "query", k: 5);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rejects_null_query()
    {
        Action act = () => new FilteredFullTextScanOperator(
            new FixedNodeListOperator(), 0, "idx", null!, k: 5);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_rejects_non_positive_k()
    {
        Action act = () => new FilteredFullTextScanOperator(
            new FixedNodeListOperator(), 0, "idx", "q", k: 0);
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
    public void Schema_has_single_NodeId_column()
    {
        var op = new FilteredFullTextScanOperator(
            new FixedNodeListOperator(), 0, IndexName, "hello", k: 3);
        op.Schema.Columns.Should().HaveCount(1);
        op.Schema.Columns[0].Type.Should().Be(TupleSlotType.NodeId);
        op.Dispose();
    }

    [Fact]
    public void Open_on_missing_index_throws_ConstraintException()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ffts_missing");
        using var tx = fx.Db.BeginTransaction();
        var op = new FilteredFullTextScanOperator(
            new FixedNodeListOperator(), 0, "no_such_index", "hello", k: 3);

        Action act = () => op.Open(((GraphTransaction)tx).Inner);
        act.Should().Throw<ConstraintException>();
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Empty_upstream_returns_no_results()
    {
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ffts_empty_up");
        fx.Db.Schema.CreateFullTextIndex(IndexName, "Doc", "body");
        using (var seed = fx.Db.BeginTransaction())
        {
            var n = seed.CreateNode("Doc");
            seed.SetProperty(n, "body", PropertyValue.FromString("hello world"));
            seed.Commit();
        }

        // 入力側が候補を 1 件も生成しない場合。
        using var tx = fx.Db.BeginTransaction();
        var source = new FixedNodeListOperator(); // no nodes
        var op = new FilteredFullTextScanOperator(source, 0, IndexName, "hello", k: 10);
        op.Open(((GraphTransaction)tx).Inner);
        op.MoveNext().Should().BeFalse();
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Filter_excludes_non_candidate_nodes()
    {
        NodeId included = default;
        NodeId excluded = default;
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ffts_filter");
        fx.Db.Schema.CreateFullTextIndex(IndexName, "Doc", "body");
        using (var seed = fx.Db.BeginTransaction())
        {
            included = seed.CreateNode("Doc");
            seed.SetProperty(included, "body", PropertyValue.FromString("hello world"));
            excluded = seed.CreateNode("Doc");
            seed.SetProperty(excluded, "body", PropertyValue.FromString("hello universe"));
            seed.Commit();
        }

        using var tx = fx.Db.BeginTransaction();
        var source = new NodeByLabelScanOperator(
            fx.Db.Schema.GetOrCreateLabel("Doc"));
        var op = new FilteredFullTextScanOperator(source, 0, IndexName, "hello", k: 10);
        op.Open(((GraphTransaction)tx).Inner);
        var count = 0;
        while (op.MoveNext()) count++;
        count.Should().Be(2);
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void No_hit_among_candidates_returns_empty()
    {
        NodeId candidate = default;
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ffts_nohit");
        fx.Db.Schema.CreateFullTextIndex(IndexName, "Doc", "body");
        using (var seed = fx.Db.BeginTransaction())
        {
            candidate = seed.CreateNode("Doc");
            seed.SetProperty(candidate, "body", PropertyValue.FromString("alpha beta"));
            seed.Commit();
        }

        using var tx = fx.Db.BeginTransaction();
        var source = new FixedNodeListOperator(candidate);
        using var result = tx.Execute(
            new FilteredFullTextScanOperator(source, 0, IndexName, "zzzzz", k: 10));
        result.Rows().Should().BeEmpty();
        tx.Rollback();
    }

    [Fact]
    public void K_limits_filtered_results()
    {
        var ids = new NodeId[5];
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ffts_klimit");
        fx.Db.Schema.CreateFullTextIndex(IndexName, "Doc", "body");
        using (var seed = fx.Db.BeginTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                ids[i] = seed.CreateNode("Doc");
                seed.SetProperty(ids[i], "body", PropertyValue.FromString("common term"));
            }
            seed.Commit();
        }

        using var tx = fx.Db.BeginTransaction();
        var source = new NodeByLabelScanOperator(
            fx.Db.Schema.GetOrCreateLabel("Doc"));
        var op = new FilteredFullTextScanOperator(source, 0, IndexName, "common", k: 2);
        op.Open(((GraphTransaction)tx).Inner);
        var count = 0;
        while (op.MoveNext()) count++;
        count.Should().Be(2);
        op.Dispose();
        tx.Rollback();
    }

    [Fact]
    public void Statistics_tracks_rows_produced()
    {
        var ids = new NodeId[3];
        using var fx = OperatorTestFixture.OpenEmpty(tag: "ffts_stats");
        fx.Db.Schema.CreateFullTextIndex(IndexName, "Doc", "body");
        using (var seed = fx.Db.BeginTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                ids[i] = seed.CreateNode("Doc");
                seed.SetProperty(ids[i], "body", PropertyValue.FromString("target word"));
            }
            seed.Commit();
        }

        using var tx = fx.Db.BeginTransaction();
        var source = new FixedNodeListOperator(ids);
        var op = new FilteredFullTextScanOperator(source, 0, IndexName, "target", k: 10);
        op.Open(((GraphTransaction)tx).Inner);
        var count = 0;
        while (op.MoveNext()) count++;
        op.Statistics.RowsProduced.Should().Be(count);
        op.Dispose();
        tx.Rollback();
    }
}
