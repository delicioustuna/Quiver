using FluentAssertions;
using Quiver;
using Quiver.Core;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// オプトイン列の登録、初期構築、永続化を <c>QuiverDatabase</c> 経由で
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
        using (var db = QuiverDatabase.Open(_path))
        {
            using (var tx = db.BeginWriteTransaction())
            {
                var a = tx.CreateVertex("A");
                var b = tx.CreateVertex("B");
                var r1 = tx.CreateEdge(a, b, "R");
                tx.SetProperty(r1, "w", PropertyValue.FromInt64(10));
                var r2 = tx.CreateEdge(a, b, "R");
                tx.SetProperty(r2, "w", PropertyValue.FromInt64(20));
                tx.Commit();
            }

            // 初回登録は true、現データから構築される。
            db.CreateColumn(EntityKind.Edge, "w").Should().BeTrue();
            // 既に列化済みなら false。
            db.CreateColumn(EntityKind.Edge, "w").Should().BeFalse();
            // 構築済みデータの projection 合計 = 10 + 20。
            db.ColumnProjectSumForTest(EntityKind.Edge, "w").Should().Be(30);
        }

        // reopen: 登録 (catalog) と列データ (head ページ) が永続している。
        using (var db2 = QuiverDatabase.Open(_path))
        {
            // 既に登録済みなので false (= catalog 永続を確認)。
            db2.CreateColumn(EntityKind.Edge, "w").Should().BeFalse();
            // 列データ (head ページ) も復元される。
            db2.ColumnProjectSumForTest(EntityKind.Edge, "w").Should().Be(30);
        }
    }

    [Fact]
    public void Drop_column_unregisters_and_persists()
    {
        using (var db = QuiverDatabase.Open(_path))
        {
            using (var tx = db.BeginWriteTransaction())
            {
                var a = tx.CreateVertex("A");
                var b = tx.CreateVertex("B");
                var r = tx.CreateEdge(a, b, "R");
                tx.SetProperty(r, "w", PropertyValue.FromInt64(42));
                tx.Commit();
            }
            db.CreateColumn(EntityKind.Edge, "w").Should().BeTrue();
            db.DropColumn(EntityKind.Edge, "w").Should().BeTrue();
            db.DropColumn(EntityKind.Edge, "w").Should().BeFalse(); // 二度目は無し
        }

        using (var db2 = QuiverDatabase.Open(_path))
        {
            // drop が永続 → 再び登録できる (true)。
            db2.CreateColumn(EntityKind.Edge, "w").Should().BeTrue();
        }
    }

    [Fact]
    public void Nexus_column_builds_from_data_and_tracks_later_writes()
    {
        NexusId first;
        VertexId a;
        VertexId b;
        using (var db = QuiverDatabase.Open(_path))
        {
            using (var tx = db.BeginWriteTransaction())
            {
                a = tx.CreateVertex("Entity");
                b = tx.CreateVertex("Entity");
                first = tx.CreateNexus("Fact", [new("Subject", a), new("Object", b)]);
                tx.SetProperty(first, "confidence", PropertyValue.FromInt64(10));
                tx.Commit();
            }

            db.CreateColumn(EntityKind.Nexus, "confidence").Should().BeTrue();
            db.ColumnProjectSumForTest(EntityKind.Nexus, "confidence").Should().Be(10);

            using (var tx = db.BeginWriteTransaction())
            {
                var second = tx.CreateNexus("Fact", [new("Subject", a), new("Object", b)]);
                tx.SetProperty(second, "confidence", PropertyValue.FromInt64(20));
                tx.Commit();
            }

            db.ColumnProjectSumForTest(EntityKind.Nexus, "confidence").Should().Be(30);
        }

        using (var reopened = QuiverDatabase.Open(_path))
        {
            reopened.CreateColumn(EntityKind.Nexus, "confidence").Should().BeFalse();
            reopened.ColumnProjectSumForTest(EntityKind.Nexus, "confidence").Should().Be(30);
        }
    }
}
