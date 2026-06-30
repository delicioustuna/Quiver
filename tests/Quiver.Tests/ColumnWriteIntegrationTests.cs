using FluentAssertions;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 列の書き込み経路を統合したエンドツーエンドテスト。列化済みキーへの
/// <see cref="IGraphTransaction.SetProperty(RelationshipId, string, in PropertyValue)"/> /
/// RemoveProperty / DeleteNode / DeleteRelationship が同じトランザクションで列を維持し、
/// 中断時には列が巻き戻り、コミット時には再オープン後も永続することを確認する。
/// 射影結果はテスト用アクセサ <c>ColumnProjectSumForTest</c> で確認する。
///
/// 列はオプトインであり、トランザクション開始時点で登録済みの場合だけ維持される。
/// <c>CreateColumn</c> は書き込みトランザクション外で行う DDL 操作なので、
/// 各テストは列を作成してから後続トランザクションで書き込む。
/// </summary>
public sealed class ColumnWriteIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ColumnWriteIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_colw_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Set_after_column_creation_is_maintained_in_tx()
    {
        using var db = GraphDatabase.Open(_path);
        // 空データに列を作ってから write する (= 純粋に write 経路で維持されることの確認)。
        db.CreateColumn(EntityKind.Relationship, "w").Should().BeTrue();

        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateNode("A");
            var b = tx.CreateNode("B");
            var r1 = tx.CreateRelationship(a, b, "R");
            tx.SetProperty(r1, "w", PropertyValue.FromInt64(10));
            var r2 = tx.CreateRelationship(a, b, "R");
            tx.SetProperty(r2, "w", PropertyValue.FromInt64(20));
            tx.Commit();
        }

        db.ColumnProjectSumForTest(EntityKind.Relationship, "w").Should().Be(30);
    }

    [Fact]
    public void Overwrite_supersedes_old_value()
    {
        using var db = GraphDatabase.Open(_path);
        RelationshipId r;
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateNode("A");
            var b = tx.CreateNode("B");
            r = tx.CreateRelationship(a, b, "R");
            tx.SetProperty(r, "w", PropertyValue.FromInt64(10));
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Relationship, "w").Should().BeTrue();
        db.ColumnProjectSumForTest(EntityKind.Relationship, "w").Should().Be(10);

        // 上書き → 旧版は delta へ退避、可視値は新値のみ。
        using (var tx = db.BeginTransaction())
        {
            tx.SetProperty(r, "w", PropertyValue.FromInt64(30));
            tx.Commit();
        }
        db.ColumnProjectSumForTest(EntityKind.Relationship, "w").Should().Be(30);
    }

    [Fact]
    public void Delete_relationship_removes_from_column()
    {
        using var db = GraphDatabase.Open(_path);
        RelationshipId r1, r2;
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateNode("A");
            var b = tx.CreateNode("B");
            r1 = tx.CreateRelationship(a, b, "R");
            tx.SetProperty(r1, "w", PropertyValue.FromInt64(10));
            r2 = tx.CreateRelationship(a, b, "R");
            tx.SetProperty(r2, "w", PropertyValue.FromInt64(20));
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Relationship, "w").Should().BeTrue();
        db.ColumnProjectSumForTest(EntityKind.Relationship, "w").Should().Be(30);

        using (var tx = db.BeginTransaction())
        {
            tx.DeleteRelationship(r2);
            tx.Commit();
        }
        db.ColumnProjectSumForTest(EntityKind.Relationship, "w").Should().Be(10);
    }

    [Fact]
    public void Remove_property_clears_column_entry()
    {
        using var db = GraphDatabase.Open(_path);
        NodeId n;
        using (var tx = db.BeginTransaction())
        {
            n = tx.CreateNode("A");
            tx.SetProperty(n, "score", PropertyValue.FromInt64(7));
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Node, "score").Should().BeTrue();
        db.ColumnProjectSumForTest(EntityKind.Node, "score").Should().Be(7);

        using (var tx = db.BeginTransaction())
        {
            tx.RemoveProperty(n, "score");
            tx.Commit();
        }
        db.ColumnProjectSumForTest(EntityKind.Node, "score").Should().Be(0);
    }

    [Fact]
    public void Abort_reverts_column_write()
    {
        using var db = GraphDatabase.Open(_path);
        RelationshipId r;
        using (var tx = db.BeginTransaction())
        {
            var a = tx.CreateNode("A");
            var b = tx.CreateNode("B");
            r = tx.CreateRelationship(a, b, "R");
            tx.SetProperty(r, "w", PropertyValue.FromInt64(10));
            tx.Commit();
        }
        db.CreateColumn(EntityKind.Relationship, "w").Should().BeTrue();
        db.ColumnProjectSumForTest(EntityKind.Relationship, "w").Should().Be(10);

        // 上書きして rollback → 列も旧値へ戻る (head 復元 + delta prune)。
        using (var tx = db.BeginTransaction())
        {
            tx.SetProperty(r, "w", PropertyValue.FromInt64(99));
            tx.Rollback();
        }
        db.ColumnProjectSumForTest(EntityKind.Relationship, "w").Should().Be(10);

        // rollback 後も正しく write できる (列状態が壊れていない)。
        using (var tx = db.BeginTransaction())
        {
            tx.SetProperty(r, "w", PropertyValue.FromInt64(50));
            tx.Commit();
        }
        db.ColumnProjectSumForTest(EntityKind.Relationship, "w").Should().Be(50);
    }

    [Fact]
    public void Maintained_writes_persist_across_reopen()
    {
        using (var db = GraphDatabase.Open(_path))
        {
            db.CreateColumn(EntityKind.Relationship, "w").Should().BeTrue();
            using var tx = db.BeginTransaction();
            var a = tx.CreateNode("A");
            var b = tx.CreateNode("B");
            var r1 = tx.CreateRelationship(a, b, "R");
            tx.SetProperty(r1, "w", PropertyValue.FromInt64(11));
            var r2 = tx.CreateRelationship(a, b, "R");
            tx.SetProperty(r2, "w", PropertyValue.FromInt64(22));
            tx.Commit();
            db.ColumnProjectSumForTest(EntityKind.Relationship, "w").Should().Be(33);
        }

        using (var db2 = GraphDatabase.Open(_path))
        {
            // 列は登録済み、head ページ (write 経路で維持された値) も復元される。
            db2.CreateColumn(EntityKind.Relationship, "w").Should().BeFalse();
            db2.ColumnProjectSumForTest(EntityKind.Relationship, "w").Should().Be(33);
        }
    }
}
