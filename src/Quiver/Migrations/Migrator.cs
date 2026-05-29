namespace Quiver.Migrations;

/// <summary>
/// OP-4: <see cref="GraphDatabase.MigrateAsync"/> から呼ばれるオーケストレータ。
/// 未適用マイグレーションを <see cref="IMigration.Version"/> 昇順 → <see cref="IMigration.Id"/>
/// Ordinal 昇順で並べ、それぞれ独立した tx で適用する。
/// </summary>
internal static class Migrator
{
    public static async Task<MigrationResult> RunAsync(
        GraphDatabase db,
        string dataDirectory,
        IEnumerable<IMigration> migrations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);

        var history = new MigrationHistory(dataDirectory);

        var ordered = migrations
            .OrderBy(m => m.Version)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToArray();

        DetectDuplicateIds(ordered);

        var applied = new List<MigrationHistoryEntry>();
        var skipped = new List<string>();

        foreach (var migration in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (history.IsApplied(migration.Id))
            {
                skipped.Add(migration.Id);
                continue;
            }

            // 各マイグレーションを独立した tx で apply。失敗時は tx 全体をロールバック。
            // - データミューテーション (CreateNode/SetProperty/...) は WAL 経由で tx 境界に乗る。
            // - スキーマミューテーション (rename / index add) は MigrationContext の OnRolledBack
            //   フックで論理的に巻き戻される (fix A)。
            // - History.Append は OnCommitted フックに乗せ、commit が WAL に durable に落ちた
            //   直後に append される (fix B)。commit 完了後・append 完了前の crash 窓は冪等性
            //   (history 不在 → 次回 re-run で同じ migration を再適用) で吸収する。
            using var tx = db.BeginTransaction();
            var ctx = new MigrationContext(tx, db.Schema, migration.Id);
            var entry = new MigrationHistoryEntry(migration.Id, migration.Version, DateTime.UtcNow);
            bool committed = false;
            tx.OnCommitted(() =>
            {
                history.Append(entry);
                committed = true;
            });
            try
            {
                await migration.ApplyAsync(ctx).ConfigureAwait(false);
                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { /* ignore */ }
                throw;
            }

            if (committed) applied.Add(entry);
        }

        return new MigrationResult(applied, skipped);
    }

    private static void DetectDuplicateIds(IMigration[] ordered)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in ordered)
        {
            if (!seen.Add(m.Id))
                throw new InvalidOperationException(
                    $"Duplicate migration Id '{m.Id}' in the supplied set.");
        }
    }
}

/// <summary>
/// OP-4: <see cref="GraphDatabase.MigrateAsync"/> の結果。新規適用 / スキップの内訳を返す。
/// </summary>
/// <param name="Applied">この呼び出しで新規適用されたマイグレーション。</param>
/// <param name="Skipped">既に <see cref="MigrationHistory"/> にあったため skip した ID。</param>
public sealed record MigrationResult(
    IReadOnlyList<MigrationHistoryEntry> Applied,
    IReadOnlyList<string> Skipped);
