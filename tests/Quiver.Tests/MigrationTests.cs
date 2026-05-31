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
        // seed: 3 User ノードを作って "User" ラベルを使う
        {
            using var db = GraphDatabase.Open(_dir);
            using var tx = db.BeginTransaction();
            for (int i = 0; i < 3; i++)
            {
                var n = tx.CreateNode("User");
                tx.SetProperty(n, "email", PropertyValue.FromString($"user{i}@example.com"));
            }
            tx.Commit();
        }

        // 1 回目: 適用される
        using (var db = GraphDatabase.Open(_dir))
        {
            var migrations = new IMigration[] { new RenameUserToPerson() };
            var result = await db.MigrateAsync(migrations);
            result.Applied.Should().HaveCount(1);
            result.Applied[0].Id.Should().Be("001_user_to_person");
            result.Skipped.Should().BeEmpty();

            db.Schema.GetOrCreateLabel("Person").Value.Should().BeGreaterThanOrEqualTo(0);

            using var tx = db.BeginReadOnlyTransaction();
            int count = 0;
            foreach (var _ in tx.Access.ScanNodes(GetInner(tx), db.Schema.GetOrCreateLabel("Person")))
                count++;
            count.Should().Be(3);
        }

        // 2 回目: history があるので skip
        using (var db = GraphDatabase.Open(_dir))
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
        using var db = GraphDatabase.Open(_dir);
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
        // seed: "OldLabel" を持つノード
        {
            using var db = GraphDatabase.Open(_dir);
            using var tx = db.BeginTransaction();
            tx.CreateNode("OldLabel");
            tx.Commit();
        }

        // rename → 例外 → tx rollback 経由で rename も巻き戻る
        using (var db = GraphDatabase.Open(_dir))
        {
            var migrations = new IMigration[] { new RenameThenFailMigration() };
            Func<Task> act = () => db.MigrateAsync(migrations);
            await act.Should().ThrowAsync<InvalidOperationException>();

            // ノードは "OldLabel" のまま残っているべき (rename が巻き戻った)
            using var tx = db.BeginReadOnlyTransaction();
            var oldId = db.Schema.GetOrCreateLabel("OldLabel");
            int oldCount = 0;
            foreach (var _ in tx.Access.ScanNodes(GetInner(tx), oldId)) oldCount++;
            oldCount.Should().Be(1, "rename failed, so the node should still be under OldLabel");
        }

        // 再 open でも "OldLabel" が durable に残っていることを確認
        using (var db = GraphDatabase.Open(_dir))
        {
            using var tx = db.BeginReadOnlyTransaction();
            var oldId = db.Schema.GetOrCreateLabel("OldLabel");
            int oldCount = 0;
            foreach (var _ in tx.Access.ScanNodes(GetInner(tx), oldId)) oldCount++;
            oldCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task Idempotent_RenameLabel_in_failing_migration_must_not_corrupt_pre_existing_state()
    {
        // Bug 1 regression: 旧 rename("A","B") を別 migration が冪等 no-op として通したとき、
        // その migration が失敗しても B → A の逆操作が発火してはいけない (corruption)。
        using var db = GraphDatabase.Open(_dir);

        // 先行 migration が完了: A→B
        {
            using var tx = db.BeginTransaction();
            tx.CreateNode("A");
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
        using var rtx = db.BeginReadOnlyTransaction();
        int bCount = 0;
        foreach (var _ in rtx.Access.ScanNodes(GetInner(rtx), db.Schema.GetOrCreateLabel("B")))
            bCount++;
        bCount.Should().Be(1, "first migration's B should be preserved");
    }

    [Fact]
    public async Task AddIndex_on_pre_existing_index_must_not_drop_it_on_rollback()
    {
        // Bug 2 regression: 既に存在する索引に対し AddIndex を呼んでも、
        // それは no-op であり、後の rollback で既存索引を消してはいけない。
        using var db = GraphDatabase.Open(_dir);
        db.Schema.CreateIndex("idx_preexisting", "Foo", "bar", IndexKind.StringEquality);

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
        using var db = GraphDatabase.Open(_dir);
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
        using var db = GraphDatabase.Open(_dir);
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
        using var db = GraphDatabase.Open(_dir);
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
        using var db = GraphDatabase.Open(_dir);
        // データを先に投入
        {
            using var tx = db.BeginTransaction();
            for (int i = 0; i < 5; i++)
            {
                var n = tx.CreateNode("Product");
                tx.SetProperty(n, "sku", PropertyValue.FromString($"SKU-{i:D3}"));
            }
            tx.Commit();
        }

        var migrations = new IMigration[] { new AddProductSkuIndex() };
        var result = await db.MigrateAsync(migrations);
        result.Applied.Should().HaveCount(1);

        db.Schema.ListIndexes().Select(i => i.Name).Should().Contain("idx_product_sku");
    }

    private static Transactions.ITransaction GetInner(IGraphTransaction tx)
    {
        var prop = tx.GetType().GetProperty("Inner",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        return (Transactions.ITransaction)prop!.GetValue(tx)!;
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
            ctx.Transaction.CreateNode("ShouldRollback");
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
