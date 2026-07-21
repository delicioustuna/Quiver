using System.Text;
using Quiver;
using FluentAssertions;
using Quiver.Core;
using Quiver.Migrations;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string _dir;

    public MigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_migration_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task RenameLabel_migration_applied_once_and_idempotent_on_rerun()
    {
        // seed: 3 User Vertexを作って "User" ラベルを使う
        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
            using var tx = db.BeginWriteTransaction();
            for (int i = 0; i < 3; i++)
            {
                var n = tx.CreateVertex("User");
                tx.SetProperty(n, "email", PropertyValue.FromString($"user{i}@example.com"));
            }
            tx.Commit();
        }

        // 1 回目: 適用される
        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            var migrations = new IMigration[] { new RenameUserToPerson() };
            var result = await db.MigrateAsync(migrations);
            result.Applied.Should().HaveCount(1);
            result.Applied[0].Id.Should().Be("001_user_to_person");
            result.Skipped.Should().BeEmpty();

            ResolveLabel(db, "Person").Value.Should().BeGreaterThanOrEqualTo(0);

            using var tx = db.BeginReadTransaction();
            int count = 0;
            foreach (var _ in tx.AsInternal().Access.ScanVertices(GetInner(tx), ResolveLabel(db, "Person")))
                count++;
            count.Should().Be(3);
        }

        // 2 回目: history があるので skip
        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            var migrations = new IMigration[] { new RenameUserToPerson() };
            var result = await db.MigrateAsync(migrations);
            result.Applied.Should().BeEmpty();
            result.Skipped.Should().ContainSingle().Which.Should().Be("001_user_to_person");
        }
    }

    [Fact]
    public async Task Migration_failure_rolls_back_data_mutations_but_does_not_record_history()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var migrations = new IMigration[] { new FailingMigration() };

        Func<Task> act = () => db.MigrateAsync(migrations);
        await act.Should().ThrowAsync<InvalidOperationException>();

        db.GetMigrationHistory().Should().BeEmpty();

        // 再実行で再試行されること (still throws)
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Migration_failure_rolls_back_label_rename_via_OnRolledBack_hook()
    {
        // seed: "OldLabel" を持つVertex
        {
            using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
            using var tx = db.BeginWriteTransaction();
            tx.CreateVertex("OldLabel");
            tx.Commit();
        }

        // rename → 例外 → tx rollback 経由で rename も巻き戻る
        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            var migrations = new IMigration[] { new RenameThenFailMigration() };
            Func<Task> act = () => db.MigrateAsync(migrations);
            await act.Should().ThrowAsync<InvalidOperationException>();

            // Vertexは "OldLabel" のまま残っているべき (rename が巻き戻った)
            using var tx = db.BeginReadTransaction();
            var oldId = ResolveLabel(db, "OldLabel");
            int oldCount = 0;
            foreach (var _ in tx.AsInternal().Access.ScanVertices(GetInner(tx), oldId)) oldCount++;
            oldCount.Should().Be(1, "rename failed, so the vertex should still be under OldLabel");
        }

        // 再 open でも "OldLabel" が durable に残っていることを確認
        using (var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver")))
        {
            using var tx = db.BeginReadTransaction();
            var oldId = ResolveLabel(db, "OldLabel");
            int oldCount = 0;
            foreach (var _ in tx.AsInternal().Access.ScanVertices(GetInner(tx), oldId)) oldCount++;
            oldCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task Idempotent_RenameLabel_in_failing_migration_must_not_corrupt_pre_existing_state()
    {
        // Bug 1 regression: 旧 rename("A","B") を別 migration が冪等 no-op として通したとき、
        // その migration が失敗しても B → A の逆操作が発火してはいけない (corruption)。
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        // 先行 migration が完了: A→B
        {
            using var tx = db.BeginWriteTransaction();
            tx.CreateVertex("A");
            tx.Commit();
        }
        await db.MigrateAsync(new IMigration[] { new VersionedMigration("first", 1, ctx =>
        {
            ctx.RenameLabel("A", "B").Should().BeTrue();
        }) });

        // 後続 migration が再度 RenameLabel("A","B") を冪等に呼びつつ失敗
        Func<Task> act = () => db.MigrateAsync(new IMigration[] { new VersionedMigration("second", 2, ctx =>
        {
            ctx.RenameLabel("A", "B"); // idempotent no-op
            throw new InvalidOperationException("intentional");
        }) });
        await act.Should().ThrowAsync<InvalidOperationException>();

        // B のままであるべき (A に巻き戻ってはいけない)
        using var rtx = db.BeginReadTransaction();
        int bCount = 0;
        foreach (var _ in rtx.AsInternal().Access.ScanVertices(GetInner(rtx), ResolveLabel(db, "B")))
            bCount++;
        bCount.Should().Be(1, "first migration's B should be preserved");
    }

    [Fact]
    public async Task AddIndex_on_pre_existing_index_must_not_drop_it_on_rollback()
    {
        // Bug 2 regression: 既に存在する索引に対し AddIndex を呼んでも、
        // それは no-op であり、後の rollback で既存索引を消してはいけない。
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_preexisting", new PropertyTarget(PropertyOwnerKind.Vertex, "bar", "Foo"), IndexKind.StringEquality)));

        Func<Task> act = () => db.MigrateAsync(new IMigration[] { new VersionedMigration("addidx", 1, ctx =>
        {
            ctx.AddIndex("idx_preexisting", "Foo", "bar", IndexKind.StringEquality); // no-op
            throw new InvalidOperationException("intentional");
        }) });
        await act.Should().ThrowAsync<InvalidOperationException>();

        db.Schema.ListIndexes().Select(i => i.Name)
            .Should().Contain("idx_preexisting",
                "pre-existing index must survive the failed migration's rollback");
    }

    [Fact]
    public async Task Migration_failure_rolls_back_AddIndex_via_OnRolledBack_hook()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var migrations = new IMigration[] { new AddIndexThenFailMigration() };
        Func<Task> act = () => db.MigrateAsync(migrations);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // 追加した索引は rollback で drop されているはず
        db.Schema.ListIndexes().Select(i => i.Name)
            .Should().NotContain("idx_temp");
    }

    [Fact]
    public async Task Multiple_migrations_applied_in_version_order()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var migrations = new IMigration[]
        {
            new VersionedMigration("c", 3, _ => { }),
            new VersionedMigration("a", 1, _ => { }),
            new VersionedMigration("b", 2, _ => { }),
        };
        var result = await db.MigrateAsync(migrations);
        result.Applied.Select(e => e.Id).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Duplicate_migration_ids_throw()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var migrations = new IMigration[]
        {
            new VersionedMigration("dup", 1, _ => { }),
            new VersionedMigration("dup", 2, _ => { }),
        };
        Func<Task> act = () => db.MigrateAsync(migrations);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddIndex_via_migration_then_seek_works()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        // データを先に投入
        {
            using var tx = db.BeginWriteTransaction();
            for (int i = 0; i < 5; i++)
            {
                var n = tx.CreateVertex("Product");
                tx.SetProperty(n, "sku", PropertyValue.FromString($"SKU-{i:D3}"));
            }
            tx.Commit();
        }

        var migrations = new IMigration[] { new AddProductSkuIndex() };
        var result = await db.MigrateAsync(migrations);
        result.Applied.Should().HaveCount(1);

        db.Schema.ListIndexes().Select(i => i.Name).Should().Contain("idx_product_sku");
    }

    // ── Empty migration ──────────────────────────────────────────

    [Fact]
    public async Task Empty_migration_completes_and_records_history()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var migrations = new IMigration[]
        {
            new VersionedMigration("noop", 1, _ => { }),
        };

        var result = await db.MigrateAsync(migrations);
        result.Applied.Should().ContainSingle().Which.Id.Should().Be("noop");
        db.GetMigrationHistory().Should().ContainSingle().Which.Id.Should().Be("noop");
    }

    // ── Partial batch: first succeeds, second fails ────────────

    [Fact]
    public async Task Partial_batch_first_succeeds_second_fails_only_first_recorded()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        var migrations = new IMigration[]
        {
            new VersionedMigration("ok", 1, ctx =>
            {
                ctx.Transaction.CreateVertex("Survivor");
            }),
            new VersionedMigration("fail", 2, ctx =>
            {
                ctx.Transaction.CreateVertex("Ghost");
                throw new InvalidOperationException("intentional");
            }),
        };

        Func<Task> act = () => db.MigrateAsync(migrations);
        await act.Should().ThrowAsync<InvalidOperationException>();

        db.GetMigrationHistory().Select(h => h.Id).Should().Equal("ok");

        using var tx = db.BeginReadTransaction();
        int survivors = 0;
        foreach (var _ in tx.AsInternal().Access.ScanVertices(
            GetInner(tx), ResolveLabel(db, "Survivor")))
            survivors++;
        survivors.Should().Be(1);

        int ghosts = 0;
        if (db.Schema.TryGetLabelId("Ghost", out var ghostLabel))
        {
            foreach (var _ in tx.AsInternal().Access.ScanVertices(GetInner(tx), ghostLabel))
                ghosts++;
        }
        ghosts.Should().Be(0, "failed migration's vertices should be rolled back");
        db.Schema.TryGetLabelId("Ghost", out _).Should().BeFalse(
            "failed migration's schema tokens share the transaction rollback boundary");
    }

    // ── Same version, different Id → Ordinal sort ──────────────

    [Fact]
    public async Task Same_version_migrations_sorted_by_id_ordinal()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        var migrations = new IMigration[]
        {
            new VersionedMigration("z_last", 1, _ => { }),
            new VersionedMigration("a_first", 1, _ => { }),
            new VersionedMigration("m_middle", 1, _ => { }),
        };
        var result = await db.MigrateAsync(migrations);
        result.Applied.Select(e => e.Id).Should().Equal("a_first", "m_middle", "z_last");
    }

    // ── RenamePropertyKey rollback ─────────────────────────────

    [Fact]
    public async Task Migration_failure_rolls_back_RenamePropertyKey()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        {
            using var tx = db.BeginWriteTransaction();
            var n = tx.CreateVertex("Item");
            tx.SetProperty(n, "old_prop", PropertyValue.FromString("val"));
            tx.Commit();
        }

        Func<Task> act = () => db.MigrateAsync(new IMigration[] { new VersionedMigration("renprop", 1, ctx =>
        {
            ctx.RenamePropertyKey("old_prop", "new_prop").Should().BeTrue();
            throw new InvalidOperationException("intentional");
        }) });
        await act.Should().ThrowAsync<InvalidOperationException>();

        db.Schema.TryGetPropertyKeyId("old_prop", out _).Should().BeTrue(
            "rename should be rolled back on failure");
    }

    // ── RenameEdgeType rollback ────────────────────────

    [Fact]
    public async Task Migration_failure_rolls_back_RenameEdgeType()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        {
            using var tx = db.BeginWriteTransaction();
            var a = tx.CreateVertex("N");
            var b = tx.CreateVertex("N");
            tx.CreateEdge(a, b, "OLD_REL");
            tx.Commit();
        }

        Func<Task> act = () => db.MigrateAsync(new IMigration[] { new VersionedMigration("renameedge", 1, ctx =>
        {
            ctx.RenameEdgeType("OLD_REL", "NEW_REL").Should().BeTrue();
            throw new InvalidOperationException("intentional");
        }) });
        await act.Should().ThrowAsync<InvalidOperationException>();

        db.Schema.TryGetEdgeTypeId("OLD_REL", out _).Should().BeTrue(
            "edge type rename should be rolled back on failure");
    }

    // ── DropIndex participates in the migration transaction ────

    [Fact]
    public async Task DropIndex_in_failed_migration_is_rolled_back()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        db.EditSchema(schema => schema.CreateIndex(new ScalarIndexDefinition("idx_doomed", new PropertyTarget(PropertyOwnerKind.Vertex, "y", "X"), IndexKind.StringEquality)));

        Func<Task> act = () => db.MigrateAsync(new IMigration[] { new VersionedMigration("dropfail", 1, ctx =>
        {
            ctx.DropIndex("idx_doomed");
            throw new InvalidOperationException("intentional");
        }) });
        await act.Should().ThrowAsync<InvalidOperationException>();

        db.Schema.ListIndexes().Select(i => i.Name).Should().Contain("idx_doomed",
            "schema and index definition changes share the migration transaction boundary");
    }

    // ── ForEachVertex inside migration ───────────────────────────

    [Fact]
    public async Task ForEachVertex_walks_all_vertices_and_mutates()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        {
            using var tx = db.BeginWriteTransaction();
            for (int i = 0; i < 4; i++)
            {
                var n = tx.CreateVertex("Sensor");
                tx.SetProperty(n, "name", PropertyValue.FromString($"s{i}"));
            }
            tx.Commit();
        }

        var result = await db.MigrateAsync(new IMigration[] { new VersionedMigration("foreach_m", 1, ctx =>
        {
            ctx.ForEachVertex("Sensor", nid =>
            {
                ctx.Transaction.SetProperty(nid, "migrated", PropertyValue.FromString("yes"));
            });
        }) });
        result.Applied.Should().ContainSingle();

        using var ro = db.BeginReadTransaction();
        int migratedCount = 0;
        foreach (var nid in ro.AsInternal().Access.ScanVertices(
            GetInner(ro), ResolveLabel(db, "Sensor")))
        {
            ro.HasProperty(nid, "migrated").Should().BeTrue();
            var v = ro.GetProperty(nid, "migrated");
            Encoding.UTF8.GetString(v.Utf8StringValue).Should().Be("yes");
            migratedCount++;
        }
        migratedCount.Should().Be(4);
    }

    // ── Migration history persists across reopen ───────────────

    [Fact]
    public async Task Migration_history_survives_close_and_reopen()
    {
        var path = System.IO.Path.Combine(_dir, "graph.quiver");

        using (var db = QuiverDatabase.Open(path))
        {
            await db.MigrateAsync(new IMigration[]
            {
                new VersionedMigration("persistent_a", 1, _ => { }),
                new VersionedMigration("persistent_b", 2, _ => { }),
            });
        }

        using (var db = QuiverDatabase.Open(path))
        {
            var result = await db.MigrateAsync(new IMigration[]
            {
                new VersionedMigration("persistent_a", 1, _ => { }),
                new VersionedMigration("persistent_b", 2, _ => { }),
            });
            result.Applied.Should().BeEmpty();
            result.Skipped.Should().Equal("persistent_a", "persistent_b");
        }
    }

    [Fact]
    public async Task Migration_history_is_stored_inside_the_database_file()
    {
        var path = System.IO.Path.Combine(_dir, "graph.quiver");
        using (var db = QuiverDatabase.Open(path))
        {
            await db.MigrateAsync(
            [
                new VersionedMigration("catalog_history", 1, _ => { }),
            ]);
        }

        File.Exists(System.IO.Path.Combine(_dir, "migrations.history")).Should().BeFalse();
        using var reopened = QuiverDatabase.Open(path);
        reopened.GetMigrationHistory()
            .Should().ContainSingle()
            .Which.Id.Should().Be("catalog_history");
    }

    // ── CancellationToken respected ────────────────────────────

    [Fact]
    public async Task MigrateAsync_respects_cancellation_token()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => db.MigrateAsync(
            new IMigration[] { new VersionedMigration("never", 1, _ => { }) },
            cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        db.GetMigrationHistory().Should().BeEmpty();
    }

    // ── MV: PropertyCardinality.Set via migration ──────────────

    [Fact]
    public async Task Migration_sets_PropertyCardinality_Set_and_schema_persists()
    {
        var path = System.IO.Path.Combine(_dir, "graph.quiver");

        using (var db = QuiverDatabase.Open(path))
        {
            var result = await db.MigrateAsync(new IMigration[] { new VersionedMigration("mv_schema", 1, ctx =>
            {
                var keyId = ctx.Schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set);
                keyId.IsValid.Should().BeTrue();
                ctx.Schema.GetPropertyKeyCardinality(keyId).Should().Be(PropertyCardinality.Set);
            }) });
            result.Applied.Should().ContainSingle();
        }

        using (var db = QuiverDatabase.Open(path))
        {
            var keyId = db.EditSchema(schema => schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));
            db.Schema.GetPropertyKeyCardinality(keyId).Should().Be(PropertyCardinality.Set);
        }
    }

    // ── MV: AddPropertyValue inside migration ──────────────────

    [Fact]
    public async Task Migration_uses_AddPropertyValue_and_values_persist()
    {
        var path = System.IO.Path.Combine(_dir, "graph.quiver");
        VertexId vertexId;

        using (var db = QuiverDatabase.Open(path))
        {
            using var tx = db.BeginWriteTransaction();
            vertexId = tx.CreateVertex("Doc");
            tx.SetProperty(vertexId, "title", PropertyValue.FromString("readme"));
            tx.Commit();
        }

        using (var db = QuiverDatabase.Open(path))
        {
            var result = await db.MigrateAsync(new IMigration[] { new VersionedMigration("mv_add", 1, ctx =>
            {
                ctx.Schema.GetOrCreatePropertyKey("labels", PropertyCardinality.Set);
                ctx.Transaction.AddPropertyValue(vertexId, "labels", PropertyValue.FromString("important"));
                ctx.Transaction.AddPropertyValue(vertexId, "labels", PropertyValue.FromString("draft"));
            }) });
            result.Applied.Should().ContainSingle();
        }

        using (var db = QuiverDatabase.Open(path))
        {
            using var ro = db.BeginReadTransaction();
            var values = CollectPropertyValues(ro.GetPropertyValues(vertexId, "labels"));
            values.Should().HaveCount(2);
            values.Should().Contain("important");
            values.Should().Contain("draft");
        }
    }

    // ── MV: AddPropertyValue rollback on failed migration ──────

    [Fact]
    public async Task Migration_failure_rolls_back_AddPropertyValue()
    {
        var path = System.IO.Path.Combine(_dir, "graph.quiver");
        VertexId vertexId;

        using (var db = QuiverDatabase.Open(path))
        {
            db.EditSchema(schema => schema.GetOrCreatePropertyKey("tags", PropertyCardinality.Set));
            using var tx = db.BeginWriteTransaction();
            vertexId = tx.CreateVertex("Item");
            tx.Commit();
        }

        using (var db = QuiverDatabase.Open(path))
        {
            Func<Task> act = () => db.MigrateAsync(new IMigration[] { new VersionedMigration("mv_fail", 1, ctx =>
            {
                ctx.Transaction.AddPropertyValue(vertexId, "tags", PropertyValue.FromString("temp"));
                throw new InvalidOperationException("intentional");
            }) });
            await act.Should().ThrowAsync<InvalidOperationException>();

            using var ro = db.BeginReadTransaction();
            var values = CollectPropertyValues(ro.GetPropertyValues(vertexId, "tags"));
            values.Should().BeEmpty("AddPropertyValue should be rolled back");
        }
    }

    // ── MV: ForEachVertex + AddPropertyValue combination ─────────

    [Fact]
    public async Task Migration_ForEachVertex_with_AddPropertyValue()
    {
        var path = System.IO.Path.Combine(_dir, "graph.quiver");

        using (var db = QuiverDatabase.Open(path))
        {
            using var tx = db.BeginWriteTransaction();
            for (int i = 0; i < 3; i++)
            {
                var n = tx.CreateVertex("Article");
                tx.SetProperty(n, "title", PropertyValue.FromString($"article_{i}"));
            }
            tx.Commit();
        }

        using (var db = QuiverDatabase.Open(path))
        {
            var result = await db.MigrateAsync(new IMigration[] { new VersionedMigration("mv_foreach", 1, ctx =>
            {
                ctx.Schema.GetOrCreatePropertyKey("category", PropertyCardinality.Set);
                ctx.ForEachVertex("Article", nid =>
                {
                    ctx.Transaction.AddPropertyValue(nid, "category", PropertyValue.FromString("tech"));
                    ctx.Transaction.AddPropertyValue(nid, "category", PropertyValue.FromString("blog"));
                });
            }) });
            result.Applied.Should().ContainSingle();

            using var ro = db.BeginReadTransaction();
            int checked_ = 0;
            foreach (var nid in ro.AsInternal().Access.ScanVertices(
                GetInner(ro), ResolveLabel(db, "Article")))
            {
                var cats = CollectPropertyValues(ro.GetPropertyValues(nid, "category"));
                cats.Should().HaveCount(2);
                cats.Should().Contain("tech");
                cats.Should().Contain("blog");
                checked_++;
            }
            checked_.Should().Be(3);
        }
    }

    // ── MV: Cardinality mismatch in migration throws ───────────

    [Fact]
    public async Task Migration_cardinality_mismatch_throws()
    {
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));

        db.EditSchema(schema => schema.GetOrCreatePropertyKey("name"));

        Func<Task> act = () => db.MigrateAsync(new IMigration[] { new VersionedMigration("mv_mismatch", 1, ctx =>
        {
            ctx.Schema.GetOrCreatePropertyKey("name", PropertyCardinality.Set);
        }) });
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Helpers ────────────────────────────────────────────────

    private static Transactions.ITransaction GetInner(IReadTransaction tx)
        => tx.AsInternal().Inner;

    private static LabelId ResolveLabel(QuiverDatabase database, string name)
    {
        database.Schema.TryGetLabelId(name, out var label).Should().BeTrue();
        return label;
    }

    private static List<string> CollectPropertyValues(PropertyValuesEnumerator enumerator)
    {
        var result = new List<string>();
        while (enumerator.MoveNext())
            result.Add(Encoding.UTF8.GetString(enumerator.Current.Utf8StringValue));
        return result;
    }

    // ----- Migration 実装 -----

    private sealed class RenameUserToPerson : IMigration
    {
        public string Id => "001_user_to_person";
        public int Version => 1;
        public Task ApplyAsync(IMigrationContext ctx)
        {
            ctx.RenameLabel("User", "Person");
            return Task.CompletedTask;
        }
    }

    private sealed class FailingMigration : IMigration
    {
        public string Id => "999_failing";
        public int Version => 99;
        public Task ApplyAsync(IMigrationContext ctx)
        {
            ctx.Transaction.CreateVertex("ShouldRollback");
            throw new InvalidOperationException("intentional failure");
        }
    }

    private sealed class RenameThenFailMigration : IMigration
    {
        public string Id => "998_rename_then_fail";
        public int Version => 98;
        public Task ApplyAsync(IMigrationContext ctx)
        {
            ctx.RenameLabel("OldLabel", "NewLabel").Should().BeTrue();
            // 直後に "NewLabel" が見えること (in-tx 観測)
            ctx.Schema.GetOrCreateLabel("NewLabel").IsValid.Should().BeTrue();
            throw new InvalidOperationException("intentional failure after rename");
        }
    }

    private sealed class AddIndexThenFailMigration : IMigration
    {
        public string Id => "997_addindex_then_fail";
        public int Version => 97;
        public Task ApplyAsync(IMigrationContext ctx)
        {
            ctx.AddIndex("idx_temp", "Foo", "bar", IndexKind.StringEquality);
            throw new InvalidOperationException("intentional failure after add index");
        }
    }

    private sealed class VersionedMigration : IMigration
    {
        private readonly Action<IMigrationContext> _body;
        public VersionedMigration(string id, int version, Action<IMigrationContext> body)
        {
            Id = id; Version = version; _body = body;
        }
        public string Id { get; }
        public int Version { get; }
        public Task ApplyAsync(IMigrationContext ctx)
        {
            _body(ctx);
            return Task.CompletedTask;
        }
    }

    private sealed class AddProductSkuIndex : IMigration
    {
        public string Id => "002_product_sku_index";
        public int Version => 2;
        public Task ApplyAsync(IMigrationContext ctx)
        {
            ctx.AddIndex("idx_product_sku", "Product", "sku", IndexKind.StringEquality);
            return Task.CompletedTask;
        }
    }
}
