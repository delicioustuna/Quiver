using System.Diagnostics;
using Yatagarasu.Api;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Benchmarks.Standalone;

/// <summary>
/// 永続的なlabel-scoped unique scalar indexの整合性と通常indexに対するコストを検証する。
/// 重複拒否、rollback、再open、backfillは製品のUnique設定を直接使用する。
/// 公開 schema または永続 definition は変更しない。
/// </summary>
internal static class UniqueConstraintRunner
{
    private const string Label = "ExternalEntity";
    private const string PropertyKey = "externalId";
    private const string IndexName = "idx_external_entity_id";
    private const int DefaultOperations = 1_000;
    private const int MeasurementRuns = 3;
    private const double MaximumOverheadRatio = 3.0;
    private const double MaximumBackfillMilliseconds = 2_000.0;

    internal static int Run(string[] args)
    {
        int operations = args.Length > 0 && int.TryParse(args[0], out int parsed)
            ? parsed
            : DefaultOperations;
        if (operations < 100)
        {
            Console.Error.WriteLine("operations は 100 以上を指定してください。");
            return 2;
        }

        Console.WriteLine("=== Unique scalar constraint benchmark ===");
        Console.WriteLine($"operations={operations:N0}, runs={MeasurementRuns}");

        try
        {
            RunCorrectnessScenarios();
            WarmUp();

            var baselineSamples = new double[MeasurementRuns];
            var uniqueSamples = new double[MeasurementRuns];
            for (int run = 0; run < MeasurementRuns; run++)
            {
                if ((run & 1) == 0)
                {
                    baselineSamples[run] = MeasureIndexedInsert(operations, enforceUnique: false);
                    uniqueSamples[run] = MeasureIndexedInsert(operations, enforceUnique: true);
                }
                else
                {
                    uniqueSamples[run] = MeasureIndexedInsert(operations, enforceUnique: true);
                    baselineSamples[run] = MeasureIndexedInsert(operations, enforceUnique: false);
                }
            }
            for (int run = 0; run < MeasurementRuns; run++)
                Console.WriteLine(FormattableString.Invariant($"sample,{run + 1},{operations},{baselineSamples[run]:F4},{uniqueSamples[run]:F4}"));
            double baselineUs = Median(baselineSamples);
            double uniqueUs = Median(uniqueSamples);
            double backfillMs = MeasureBackfill(operations);
            double overheadRatio = uniqueUs / baselineUs;

            bool performancePass = overheadRatio <= MaximumOverheadRatio
                && backfillMs <= MaximumBackfillMilliseconds;

            Console.WriteLine("correctness: PASS");
            Console.WriteLine($"indexed insert       : {baselineUs:F2} us/op");
            Console.WriteLine($"unique indexed insert: {uniqueUs:F2} us/op");
            Console.WriteLine($"overhead ratio       : {overheadRatio:F2}x (limit {MaximumOverheadRatio:F2}x)");
            Console.WriteLine($"backfill             : {backfillMs:F2} ms (limit {MaximumBackfillMilliseconds:F0} ms)");
            Console.WriteLine($"result               : {(performancePass ? "PASS" : "FAIL")}");
            Console.WriteLine(
                $"csv,unique_constraint,{operations},{baselineUs:F4},{uniqueUs:F4}," +
                $"{overheadRatio:F4},{backfillMs:F4},{(performancePass ? "PASS" : "FAIL")}");
            return performancePass ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"correctness: FAIL ({ex.GetType().Name}: {ex.Message})");
            return 1;
        }
    }

    private static void RunCorrectnessScenarios()
    {
        string directory = BenchTempDir.Create("unique_constraint_correctness");
        string path = Path.Combine(directory, "graph.yata");
        try
        {
            VertexId alphaOwner;
            VertexId betaOwner;
            using (var database = YatagarasuDatabase.Open(path))
            {
                using (var schema = database.BeginWriteTransaction())
                {
                    CreateIndex(schema);
                    schema.Commit();
                }

                using (var seed = database.BeginWriteTransaction())
                {
                    alphaOwner = seed.CreateVertex(Label);
                    betaOwner = seed.CreateVertex(Label);
                    SetUniqueString(seed, alphaOwner, "alpha");
                    SetUniqueString(seed, betaOwner, "beta");
                    seed.Commit();
                }

                using (var conflict = database.BeginWriteTransaction())
                {
                    SetUniqueString(conflict, alphaOwner, "alpha");
                    VertexId other = conflict.CreateVertex(Label);
                    ExpectConflict(() => SetUniqueString(conflict, other, "alpha"));
                    conflict.Rollback();
                }

                using (var intraTransactionConflict = database.BeginWriteTransaction())
                {
                    VertexId first = intraTransactionConflict.CreateVertex(Label);
                    VertexId second = intraTransactionConflict.CreateVertex(Label);
                    SetUniqueString(intraTransactionConflict, first, "provisional-value");
                    ExpectConflict(() => SetUniqueString(
                        intraTransactionConflict,
                        second,
                        "provisional-value"));
                    intraTransactionConflict.Rollback();
                }

                using (var fallbackConflict = database.BeginWriteTransaction())
                {
                    fallbackConflict.AsInternal().Inner.Indexes.SetIndexState(
                        IndexName,
                        IndexLifecycleState.RebuildRequired);
                    VertexId other = fallbackConflict.CreateVertex(Label);
                    ExpectConflict(() => SetUniqueString(
                        fallbackConflict,
                        other,
                        "alpha"));
                    fallbackConflict.Rollback();
                }

                using (var reuseAfterRemove = database.BeginWriteTransaction())
                {
                    reuseAfterRemove.RemoveProperty(alphaOwner, PropertyKey);
                    VertexId replacement = reuseAfterRemove.CreateVertex(Label);
                    SetUniqueString(reuseAfterRemove, replacement, "alpha");
                    reuseAfterRemove.Commit();
                }

                using (var reuseAfterDelete = database.BeginWriteTransaction())
                {
                    reuseAfterDelete.DeleteVertex(betaOwner);
                    VertexId replacement = reuseAfterDelete.CreateVertex(Label);
                    SetUniqueString(reuseAfterDelete, replacement, "beta");
                    reuseAfterDelete.Commit();
                }

                using (var savepoint = database.BeginWriteTransaction())
                {
                    SavepointId boundary = savepoint.Savepoint("before-provisional-key");
                    VertexId provisional = savepoint.CreateVertex(Label);
                    SetUniqueString(savepoint, provisional, "savepoint-value");
                    savepoint.RollbackTo(boundary);

                    VertexId survivor = savepoint.CreateVertex(Label);
                    SetUniqueString(savepoint, survivor, "savepoint-value");
                    savepoint.Commit();
                }

                using (var aborted = database.BeginWriteTransaction())
                {
                    VertexId provisional = aborted.CreateVertex(Label);
                    SetUniqueString(aborted, provisional, "aborted-value");
                    aborted.Rollback();
                }
            }

            using (var reopened = YatagarasuDatabase.Open(path))
            {
                using (var reuseAfterAbort = reopened.BeginWriteTransaction())
                {
                    VertexId survivor = reuseAfterAbort.CreateVertex(Label);
                    SetUniqueString(reuseAfterAbort, survivor, "aborted-value");
                    reuseAfterAbort.Commit();
                }

                using var committedConflict = reopened.BeginWriteTransaction();
                VertexId other = committedConflict.CreateVertex(Label);
                ExpectConflict(() => SetUniqueString(
                    committedConflict,
                    other,
                    "aborted-value"));
                committedConflict.Rollback();
            }

            RunBackfillCorrectness();
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    private static void RunBackfillCorrectness()
    {
        string validDirectory = BenchTempDir.Create("unique_constraint_backfill_valid");
        string duplicateDirectory = BenchTempDir.Create("unique_constraint_backfill_duplicate");
        try
        {
            string validPath = Path.Combine(validDirectory, "graph.yata");
            using (var database = YatagarasuDatabase.Open(validPath))
            {
                SeedWithoutIndex(database, 32, duplicateLast: false);
                using var backfill = database.BeginWriteTransaction();
                ValidateAndCreateUniqueStringIndex(backfill);
                backfill.Commit();
            }

            using (var reopened = YatagarasuDatabase.Open(validPath))
            using (var read = reopened.BeginReadTransaction())
            {
                Assert(
                    CountOwners(read, "external-00000031") == 1,
                    "successful backfill was not durable across reopen");
            }

            string duplicatePath = Path.Combine(duplicateDirectory, "graph.yata");
            using (var database = YatagarasuDatabase.Open(duplicatePath))
            {
                SeedWithoutIndex(database, 32, duplicateLast: true);
                using var backfill = database.BeginWriteTransaction();
                ExpectConflict(() => ValidateAndCreateUniqueStringIndex(backfill));
                backfill.Rollback();
                Assert(
                    !database.Schema.IndexExists(IndexName),
                    "failed duplicate backfill published an index definition");
            }

            using (var reopened = YatagarasuDatabase.Open(duplicatePath))
            {
                Assert(
                    !reopened.Schema.IndexExists(IndexName),
                    "failed duplicate backfill survived reopen as a published definition");
            }
        }
        finally
        {
            BenchTempDir.Delete(validDirectory);
            BenchTempDir.Delete(duplicateDirectory);
        }
    }

    private static void WarmUp()
    {
        _ = MeasureIndexedInsert(100, enforceUnique: false);
        _ = MeasureIndexedInsert(100, enforceUnique: true);
        _ = MeasureBackfill(100);
    }

    private static double MeasureIndexedInsert(int operations, bool enforceUnique)
    {
        string directory = BenchTempDir.Create(
            enforceUnique ? "unique_constraint_enforced" : "unique_constraint_baseline");
        string path = Path.Combine(directory, "graph.yata");
        try
        {
            using var database = YatagarasuDatabase.Open(path);
            using (var schema = database.BeginWriteTransaction())
            {
                CreateIndex(schema, unique: enforceUnique);
                schema.Commit();
            }

            var stopwatch = Stopwatch.StartNew();
            using (var write = database.BeginWriteTransaction())
            {
                for (int i = 0; i < operations; i++)
                {
                    VertexId owner = write.CreateVertex(Label);
                    string value = $"external-{i:D8}";
                    if (enforceUnique)
                    {
                        SetUniqueString(write, owner, value);
                    }
                    else
                    {
                        PropertyValue property = PropertyValue.FromString(value);
                        write.SetProperty(owner, PropertyKey, in property);
                    }
                }
                write.Commit();
            }
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds * 1_000.0 / operations;
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    private static double MeasureBackfill(int operations)
    {
        string directory = BenchTempDir.Create("unique_constraint_backfill");
        string path = Path.Combine(directory, "graph.yata");
        try
        {
            using var database = YatagarasuDatabase.Open(path);
            SeedWithoutIndex(database, operations, duplicateLast: false);

            var stopwatch = Stopwatch.StartNew();
            using (var write = database.BeginWriteTransaction())
            {
                ValidateAndCreateUniqueStringIndex(write);
                write.Commit();
            }
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds;
        }
        finally
        {
            BenchTempDir.Delete(directory);
        }
    }

    private static void SeedWithoutIndex(
        YatagarasuDatabase database,
        int count,
        bool duplicateLast)
    {
        using var write = database.BeginWriteTransaction();
        for (int i = 0; i < count; i++)
        {
            VertexId owner = write.CreateVertex(Label);
            int valueNumber = duplicateLast && i == count - 1 ? 0 : i;
            PropertyValue value = PropertyValue.FromString(
                $"external-{valueNumber:D8}");
            write.SetProperty(owner, PropertyKey, in value);
        }
        write.Commit();
    }

    private static void CreateIndex(IWriteTransaction transaction, bool unique = true)
        => transaction.EditSchema.CreateIndex(new ScalarIndexDefinition(
            IndexName,
            new PropertyTarget(
                PropertyOwnerKind.Vertex,
                PropertyKey,
                Label),
            IndexKind.StringEquality, Unique: unique));

    private static void SetUniqueString(
        IWriteTransaction transaction,
        VertexId owner,
        string value)
    {
        PropertyValue property = PropertyValue.FromString(value);
        transaction.SetProperty(owner, PropertyKey, in property);
    }

    private static void ValidateAndCreateUniqueStringIndex(IWriteTransaction transaction)
        => CreateIndex(transaction);
    private static int CountOwners(IReadTransaction transaction, string value)
    {
        PropertyValue property = PropertyValue.FromString(value);
        EntityRefEnumerator matches = transaction.SeekIndex(IndexName, in property);
        try
        {
            int count = 0;
            while (matches.MoveNext())
                count++;
            return count;
        }
        finally
        {
            matches.Dispose();
        }
    }

    private static void ExpectConflict(Action operation)
    {
        try
        {
            operation();
        }
        catch (UniqueConstraintViolationException)
        {
            return;
        }

        throw new InvalidOperationException("expected a unique conflict");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        return values[values.Length / 2];
    }


}
