namespace Yatagarasu.Migrations;

/// <summary>
/// <see cref="YatagarasuDatabase.MigrateAsync"/> から呼ばれるオーケストレータ。
/// 未適用マイグレーションを <see cref="IMigration.Version"/> 昇順 → <see cref="IMigration.Id"/>
/// Ordinal 昇順で並べ、それぞれ独立した tx で適用する。
/// </summary>
internal static class Migrator
{
    public static async Task<MigrationResult> RunAsync(
        YatagarasuDatabase db,
        IEnumerable<IMigration> migrations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(migrations);
        var appliedIds = db.GetMigrationHistory()
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);

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

            if (appliedIds.Contains(migration.Id))
            {
                skipped.Add(migration.Id);
                continue;
            }

            // データ、schema catalog、migration history を同じ page-WAL transaction に載せる。
            // 履歴だけを後から追記する crash 窓を作らないため、commit 前に primary catalog を更新する。
            using var tx = db.BeginWriteTransaction();
            var ctx = new MigrationContext(tx, tx.EditSchema, migration.Id);
            var entry = new MigrationHistoryEntry(migration.Id, migration.Version, DateTime.UtcNow);
            try
            {
                await migration.ApplyAsync(ctx).ConfigureAwait(false);
                tx.AsInternal().Inner.Indexes.AppendMigrationHistory(entry);
                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { /* ignore */ }
                throw;
            }

            applied.Add(entry);
            appliedIds.Add(entry.Id);
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
/// <see cref="YatagarasuDatabase.MigrateAsync"/> の結果。新規適用 / スキップの内訳を返す。
/// </summary>
/// <param name="Applied">この呼び出しで新規適用されたマイグレーション。</param>
/// <param name="Skipped">primary catalog に記録済みだったため skip した ID。</param>
public sealed record MigrationResult(
    IReadOnlyList<MigrationHistoryEntry> Applied,
    IReadOnlyList<string> Skipped);
