using FluentAssertions;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// オプトイン列の登録、初期構築、永続化を <c>GraphDatabase</c> 経由で
/// エンドツーエンドに検証する。
/// 射影結果はテスト用アクセサ <c>ColumnProjectSumForTest</c> で確認する。
/// </summary>
public sealed class ColumnRegistrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ColumnRegistrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_col_" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Create_column_builds_from_data_and_persists_across_reopen()
    {
        using (var db = GraphDatabase.Open(_path))
        {
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

            // 初回登録は true、現データから構築される。
            db.CreateColumn(EntityKind.Relationship, "w").Should().BeTrue();
            // 既に列化済みなら false。
            db.CreateColumn(EntityKind.Relationship, "w").Should().BeFalse();
            // 構築済みデータの projection 合計 = 10 + 20。
            db.ColumnProjectSumForTest(EntityKind.Relationship, "w").Should().Be(30);
        }

        // reopen: 登録 (catalog) と列データ (head ページ) が永続している。
        using (var db2 = GraphDatabase.Open(_path))
        {
            // 既に登録済みなので false (= catalog 永続を確認)。
            db2.CreateColumn(EntityKind.Relationship, "w").Should().BeFalse();
            // 列データ (head ページ) も復元される。
            db2.ColumnProjectSumForTest(EntityKind.Relationship, "w").Should().Be(30);
        }
    }

    [Fact]
    public void Drop_column_unregisters_and_persists()
    {
        using (var db = GraphDatabase.Open(_path))
        {
            using (var tx = db.BeginTransaction())
            {
                var a = tx.CreateNode("A");
                var b = tx.CreateNode("B");
                var r = tx.CreateRelationship(a, b, "R");
                tx.SetProperty(r, "w", PropertyValue.FromInt64(42));
                tx.Commit();
            }
            db.CreateColumn(EntityKind.Relationship, "w").Should().BeTrue();
            db.DropColumn(EntityKind.Relationship, "w").Should().BeTrue();
            db.DropColumn(EntityKind.Relationship, "w").Should().BeFalse(); // 二度目は無し
        }

        using (var db2 = GraphDatabase.Open(_path))
        {
            // drop が永続 → 再び登録できる (true)。
            db2.CreateColumn(EntityKind.Relationship, "w").Should().BeTrue();
        }
    }

    [Fact]
    public void Hyperedge_column_builds_from_data_and_tracks_later_writes()
    {
        HyperedgeId first;
        NodeId a;
        NodeId b;
        using (var db = GraphDatabase.Open(_path))
        {
            using (var tx = db.BeginTransaction())
            {
                a = tx.CreateNode("Entity");
                b = tx.CreateNode("Entity");
                first = tx.CreateHyperedge("Fact", [new("Subject", a), new("Object", b)]);
                tx.SetProperty(first, "confidence", PropertyValue.FromInt64(10));
                tx.Commit();
            }

            db.CreateColumn(EntityKind.Hyperedge, "confidence").Should().BeTrue();
            db.ColumnProjectSumForTest(EntityKind.Hyperedge, "confidence").Should().Be(10);

            using (var tx = db.BeginTransaction())
            {
                var second = tx.CreateHyperedge("Fact", [new("Subject", a), new("Object", b)]);
                tx.SetProperty(second, "confidence", PropertyValue.FromInt64(20));
                tx.Commit();
            }

            db.ColumnProjectSumForTest(EntityKind.Hyperedge, "confidence").Should().Be(30);
        }

        using (var reopened = GraphDatabase.Open(_path))
        {
            reopened.CreateColumn(EntityKind.Hyperedge, "confidence").Should().BeFalse();
            reopened.ColumnProjectSumForTest(EntityKind.Hyperedge, "confidence").Should().Be(30);
        }
    }
}
