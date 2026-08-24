using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Transactions;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class SingleWriterContractTests : IDisposable
{
    private readonly string _directory;
    private readonly YatagarasuDatabase _database;

    public SingleWriterContractTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "yatagarasu_single_writer_" + Guid.NewGuid().ToString("N"));
        _database = YatagarasuDatabase.Open(Path.Combine(_directory, "graph.yata"), new YatagarasuDatabaseOptions
        {
            WriterContentionMode = WriterContentionMode.FailFast,
            WriterWaitTimeout = TimeSpan.FromMilliseconds(100),
        });
    }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Facade_backend_and_manager_share_the_same_fail_fast_lease()
    {
        using var first = _database.BeginWriteTransaction();

        Action facade = () => _database.BeginWriteTransaction().Dispose();
        Action backend = () => _database.BackendInternal.BeginWriteTransaction().Dispose();
        Action manager = () => _database.BackendInternal.Transactions.BeginWrite().Dispose();

        facade.Should().Throw<WriterBusyException>();
        backend.Should().Throw<WriterBusyException>();
        manager.Should().Throw<WriterBusyException>();
        first.Rollback();
    }

    [Fact]
    public void Bulk_and_schema_mutations_cannot_bypass_an_active_writer()
    {
        using (var first = _database.BeginWriteTransaction())
        {
            Action bulk = () => _database.BackendInternal.BulkLoad.BeginBinaryBulkLoad!(false).Dispose();
            Action schema = () => _database.EditSchema(schema => schema.GetOrCreateLabel("blocked"));
            bulk.Should().Throw<WriterBusyException>();
            schema.Should().Throw<WriterBusyException>();
            first.Rollback();
        }

        using (var loader = _database.BackendInternal.BulkLoad.BeginBinaryBulkLoad!(false))
        {
            Action secondWriter = () => _database.BeginWriteTransaction().Dispose();
            secondWriter.Should().Throw<WriterBusyException>();
        }

        using var availableAgain = _database.BeginWriteTransaction();
        availableAgain.Rollback();
    }

    [Fact]
    public async Task Waiting_mode_hands_the_lease_to_the_next_writer_after_release()
    {
        string directory = Path.Combine(Path.GetTempPath(), "yatagarasu_writer_wait_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var database = YatagarasuDatabase.Open(Path.Combine(directory, "graph.yata"), new YatagarasuDatabaseOptions
            {
                WriterWaitTimeout = TimeSpan.FromSeconds(2),
            });
            using var first = database.BeginWriteTransaction();
            Task<IWriteTransaction> waiting = Task.Run(() => database.BeginWriteTransaction());
            await Task.Delay(100);
            waiting.IsCompleted.Should().BeFalse();

            first.Rollback();
            using IWriteTransaction second = await waiting;
            second.Rollback();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Same_transaction_rejects_a_concurrent_execution_flow_but_allows_await_continuation()
    {
        ITransaction transaction = _database.BackendInternal.Transactions.BeginRead();
        using (transaction)
        {
            using (transaction.EnterUsage())
            {
                await Task.Yield();
                transaction.State.Should().Be(TransactionState.Active);
            }

            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Task holder = Task.Run(() =>
            {
                using var usage = transaction.EnterUsage();
                entered.Set();
                release.Wait();
            });
            entered.Wait();
            Action concurrent = () => transaction.EnterUsage().Dispose();
            concurrent.Should().Throw<ConcurrentTransactionUseException>();
            release.Set();
            await holder;
        }
    }

    [Fact]
    public void Default_writer_wait_is_five_seconds()
        => new YatagarasuDatabaseOptions().WriterWaitTimeout.Should().Be(TimeSpan.FromSeconds(5));
}
