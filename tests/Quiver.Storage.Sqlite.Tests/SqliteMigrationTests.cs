using FluentAssertions;
using Quiver.Core;
using Quiver.Migrations;
using Xunit;

namespace Quiver.Storage.Sqlite.Tests;

/// <summary>
/// OP-4: SQLite backend で migration が正しく tx 境界に乗ることを検証する。
/// schema rename は SQL の native tx で巻き戻り、commit 時のみ history に append される。
/// </summary>
public sealed class SqliteMigrationTests : IDisposable
{
    private readonly string _dir;

    public SqliteMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_sqlite_migration_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private GraphDatabase Open() => GraphDatabase.Open(_dir, new GraphDatabaseOptions
    {
        BackendFactory = new SqliteGraphStorageBackendFactory(),
    });

    [Fact]
    public async Task SQLite_rename_label_via_migration_applies_and_persists()
    {
        // seed: User ノードを 2 個
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            tx.CreateNode("User");
            tx.CreateNode("User");
            tx.Commit();
        }

        // migrate User → Person
        using (var db = Open())
        {
            var result = await db.MigrateAsync(new IMigration[] { new RenameUserToPerson() });
            result.Applied.Should().HaveCount(1);

            // 同 process 内で Person ラベルが解決できる
            db.Schema.GetLabelName(db.Schema.GetOrCreateLabel("Person")).Should().Be("Person");
        }

        // 再 open で durable に Person が残ること
        using (var db = Open())
        {
            db.Schema.GetLabelName(db.Schema.GetOrCreateLabel("Person")).Should().Be("Person");
            db.GetMigrationHistory().Should().ContainSingle().Which.Id.Should().Be("rename_user_person_sqlite");
        }

        // 再実行で skip
        using (var db = Open())
        {
            var result = await db.MigrateAsync(new IMigration[] { new RenameUserToPerson() });
            result.Applied.Should().BeEmpty();
            result.Skipped.Should().ContainSingle();
        }
    }

    [Fact]
    public async Task SQLite_migration_failure_rolls_back_label_rename_via_SQL_tx()
    {
        // seed
        using (var db = Open())
        {
            using var tx = db.BeginTransaction();
            tx.CreateNode("OldLabel");
            tx.Commit();
        }

        using (var db = Open())
        {
            var migrations = new IMigration[] { new RenameThenFail() };
            Func<Task> act = () => db.MigrateAsync(migrations);
            await act.Should().ThrowAsync<InvalidOperationException>();

            // SQL の native rollback で OldLabel が残っているはず
            db.Schema.GetLabelName(db.Schema.GetOrCreateLabel("OldLabel")).Should().Be("OldLabel");

            // 不正 cache が混入していないこと: NewLabel は新 ID として "別の" ラベルになるはず
            // (rename が rolled back されたので "NewLabel" は元 DB 上の OldLabel と同じ ID では無い)
            var newId = db.Schema.GetOrCreateLabel("NewLabel");
            var oldId = db.Schema.GetOrCreateLabel("OldLabel");
            newId.Value.Should().NotBe(oldId.Value);

            // history は空
            db.GetMigrationHistory().Should().BeEmpty();
        }
    }

    private sealed class RenameUserToPerson : IMigration
    {
        public string Id => "rename_user_person_sqlite";
        public int Version => 1;
        public Task ApplyAsync(IMigrationContext ctx)
        {
            ctx.RenameLabel("User", "Person");
            return Task.CompletedTask;
        }
    }

    private sealed class RenameThenFail : IMigration
    {
        public string Id => "rename_then_fail_sqlite";
        public int Version => 1;
        public Task ApplyAsync(IMigrationContext ctx)
        {
            ctx.RenameLabel("OldLabel", "NewLabel");
            throw new InvalidOperationException("intentional");
        }
    }
}
