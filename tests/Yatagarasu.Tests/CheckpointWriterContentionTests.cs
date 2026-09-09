using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class CheckpointWriterContentionTests
{
    private static YatagarasuDatabaseOptions Options(WriterContentionMode mode) => new()
    {
        CheckpointThresholdBytes = 1,
        WriterContentionMode = mode,
        WriterWaitTimeout = TimeSpan.FromMilliseconds(200),
    };

    [Theory]
    [InlineData(WriterContentionMode.FailFast)]
    [InlineData(WriterContentionMode.Wait)]
    public void Threshold_checkpoint_losing_the_writer_lease_does_not_fault_the_instance(
        WriterContentionMode mode)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "yatagarasu_ckpt_contention_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var database = YatagarasuDatabase.Open(
                Path.Combine(directory, "graph.yata"), Options(mode));

            for (int i = 0; i < 8; i++)
            {
                using var tx = database.BeginWriteTransaction();
                var vertex = tx.CreateVertex("Node");
                tx.SetProperty(vertex, "payload", PropertyValue.FromString(new string('x', 1024)));
                tx.Commit();
            }

            var manager = (TransactionManager)database.BackendInternal.Transactions;
            using (var holder = database.BeginWriteTransaction())
            {
                Action opportunisticCheckpoint = manager.AfterWriteCommitted;
                opportunisticCheckpoint.Should().NotThrow();
                manager.IsFaulted.Should().BeFalse();
                holder.Rollback();
            }

            using (var read = database.BeginReadTransaction())
                read.Should().NotBeNull();

            using (var write = database.BeginWriteTransaction())
            {
                var vertex = write.CreateVertex("AfterContention");
                write.SetProperty(vertex, "ok", PropertyValue.FromBool(true));
                write.Commit();
            }

            manager.IsFaulted.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Concurrent_fail_fast_writers_never_fault_the_instance()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "yatagarasu_ckpt_contention_par_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var database = YatagarasuDatabase.Open(
                Path.Combine(directory, "graph.yata"), Options(WriterContentionMode.FailFast));

            int commits = 0;
            var unexpected = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var threads = new Thread[4];
            using var start = new Barrier(threads.Length);

            for (int t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() =>
                {
                    start.SignalAndWait();
                    for (int attempt = 0; attempt < 100; attempt++)
                    {
                        try
                        {
                            using var tx = database.BeginWriteTransaction();
                            var vertex = tx.CreateVertex("Node");
                            tx.SetProperty(
                                vertex,
                                "payload",
                                PropertyValue.FromString(new string('x', 512)));
                            tx.Commit();
                            Interlocked.Increment(ref commits);
                        }
                        catch (WriterBusyException)
                        {
                        }
                        catch (Exception exception)
                        {
                            unexpected.Add(exception);
                            return;
                        }
                    }
                });
                threads[t].Start();
            }

            foreach (Thread thread in threads) thread.Join();

            unexpected.Should().BeEmpty();
            ((TransactionManager)database.BackendInternal.Transactions).IsFaulted.Should().BeFalse();
            Volatile.Read(ref commits).Should().BeGreaterThan(0);

            using var verify = database.BeginReadTransaction();
            verify.Should().NotBeNull();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
