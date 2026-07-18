// Quiver.Samples.Migration— スキーママイグレーションのデモ。
// v1 → v2 で `User` ラベルを `Person` にリネームし、email 索引を追加する。

using Quiver;
using Quiver.Api;
using Quiver.Migrations;
using Quiver.Storage.Records;

var dir = Path.Combine(Path.GetTempPath(), "quiver_sample_migration_" + Guid.NewGuid().ToString("N"));
Console.WriteLine($"Data directory: {dir}");

try
{
    // ─── v1 スキーマでデータを投入 ───────────────────────────
    using (var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver")))
    {
        Console.WriteLine();
        Console.WriteLine("[v1 schema] seeding 3 User vertices");
        using var tx = db.BeginWriteTransaction();
        for (int i = 0; i < 3; i++)
        {
            var n = tx.CreateVertex("User");
            tx.SetProperty(n, "email", PropertyValue.FromString($"user{i}@example.com"));
        }
        tx.Commit();
    }

    // ─── v2 へマイグレーション ───────────────────────────────
    using (var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver")))
    {
        Console.WriteLine();
        Console.WriteLine("[migrate] applying v1 → v2 migration");
        var result = await db.MigrateAsync(new IMigration[]
        {
            new V1ToV2_UserToPersonWithEmailIndex(),
        });

        Console.WriteLine($"  applied = [{string.Join(", ", result.Applied.Select(e => e.Id))}]");
        Console.WriteLine($"  skipped = [{string.Join(", ", result.Skipped)}]");
    }

    // ─── v2 スキーマで検証 ─────────────────────────────────
    using (var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver")))
    {
        Console.WriteLine();
        Console.WriteLine("[v2 schema] verifying Person label + email index");

        using var tx = db.BeginReadTransaction();
        // 公開 DSL でラベル別にVertexを数える (g.V().HasLabel(...).Count())。
        long personCount = tx.Query.Vertices().HasLabel("Person").Count();
        Console.WriteLine($"  Person count = {personCount}");

        var indexes = db.Schema.ListIndexes()
            .Select(i => $"{i.Name}({i.Target.Scope}.{i.Target.PropertyKey}, {i.Kind})")
            .ToArray();
        Console.WriteLine($"  indexes = [{string.Join(", ", indexes)}]");

        // 索引を引いて 1 件取れることを確認
        using var seek = tx.SeekIndex("idx_person_email",
            PropertyValue.FromString("user1@example.com"));
        while (seek.MoveNext())
            Console.WriteLine($"  index lookup hit vertexId={seek.Current.Value}");
    }

    // ─── 同じマイグレーションを再実行しても skip される (冪等性) ──
    using (var db = QuiverDatabase.Open(System.IO.Path.Combine(dir, "graph.quiver")))
    {
        Console.WriteLine();
        Console.WriteLine("[re-run] applying the same migration again — should be a no-op");
        var result = await db.MigrateAsync(new IMigration[]
        {
            new V1ToV2_UserToPersonWithEmailIndex(),
        });
        Console.WriteLine($"  applied = [{string.Join(", ", result.Applied.Select(e => e.Id))}]");
        Console.WriteLine($"  skipped = [{string.Join(", ", result.Skipped)}]");

        Console.WriteLine();
        Console.WriteLine("[history]");
        foreach (var e in db.GetMigrationHistory())
            Console.WriteLine($"  {e.Id} (v{e.Version}) applied at {e.AppliedAtUtc:O}");
    }
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}

internal sealed class V1ToV2_UserToPersonWithEmailIndex : IMigration
{
    public string Id => "v1_to_v2_user_to_person_and_email_index";
    public int Version => 2;

    public Task ApplyAsync(IMigrationContext ctx)
    {
        // 1. ラベル名を rename (User → Person)。
        //    ラベル ID は維持されるので既存Vertexは新名で参照可能。
        ctx.RenameLabel("User", "Person");

        // 2. email プロパティキー用の文字列等値インデックスを作成。
        ctx.AddIndex("idx_person_email", "Person", "email", IndexKind.StringEquality);

        // 3. 既存Vertexを索引に backfill。
        ctx.ForEachVertex("Person", vertexId =>
        {
            var email = ctx.Transaction.GetProperty(vertexId, "email");
            if (email.Type == PropertyValueType.String)
                ctx.Transaction.SetProperty(vertexId, "email", email);
        });

        return Task.CompletedTask;
    }
}
