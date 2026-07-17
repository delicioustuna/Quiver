using FluentAssertions;
using Quiver.Core;
using Quiver.Query.Physical;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

public sealed class TransactionRuntimeContractTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public TransactionRuntimeContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_tx_contract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Transaction_context_flows_across_task_boundary()
    {
        VertexId vertexId;
        using (var db = QuiverDatabase.Open(_path))
        {
            using (var seed = db.BeginTransaction())
            {
                vertexId = seed.CreateVertex("Doc");
                seed.SetProperty(vertexId, "name", PropertyValue.FromString("before"));
                seed.Commit();
            }

            using (var tx = db.BeginTransaction())
            {
                await Task.Run(() =>
                    tx.SetProperty(vertexId, "name", PropertyValue.FromString("after")));
                tx.Rollback();
            }

            using var read = db.BeginReadOnlyTransaction();
            var value = read.GetProperty(vertexId, "name");
            System.Text.Encoding.UTF8.GetString(value.Utf8StringValue).Should().Be("before");
        }
    }

    [Fact]
    public async Task BeginTransaction_waits_for_active_writer_by_default()
    {
        using var db = QuiverDatabase.Open(_path, new QuiverDatabaseOptions
        {
            LockTimeout = TimeSpan.FromSeconds(2),
        });

        using var first = db.BeginTransaction();
        first.CreateVertex("Held");

        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(() =>
        {
            using var tx = db.BeginTransaction();
            secondStarted.SetResult();
            tx.CreateVertex("Released");
            tx.Commit();
        });

        await Task.Delay(100);
        secondStarted.Task.IsCompleted.Should().BeFalse();

        first.Commit();
        await second;
        secondStarted.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public void EnforceExclusiveWriter_rejects_active_writer_without_waiting()
    {
        using var db = QuiverDatabase.Open(_path, new QuiverDatabaseOptions
        {
            EnforceExclusiveWriter = true,
        });

        using var first = db.BeginTransaction();

        Action act = () =>
        {
            using var _ = db.BeginTransaction();
        };

        act.Should().Throw<TransactionException>();
    }

    [Fact]
    public void Vector_autocommit_mutations_use_writer_gate()
    {
        using var db = QuiverDatabase.Open(_path, new QuiverDatabaseOptions
        {
            EnforceExclusiveWriter = true,
        });
        var keyId = db.Schema.GetOrCreatePropertyKey("embedding");

        using var first = db.BeginTransaction();

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                db.Vectors.CreateVectorIndex(new VectorIndexSpec(
                    "held_writer_vectors",
                    EntityKind.Vertex,
                    keyId,
                    Dimensions: 4,
                    DistanceMetric.Cosine,
                    ProviderId: "test"));
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        using (ExecutionContext.SuppressFlow())
        {
            thread.Start();
        }
        thread.Join();

        error.Should().BeOfType<TransactionException>();
    }

    [Fact]
    public async Task Same_transaction_handle_concurrent_use_throws()
    {
        using var db = QuiverDatabase.Open(_path);
        using var tx = db.BeginTransaction();
        var op = new BlockingOperator();

        var running = Task.Run(() => tx.Execute(op));
        op.Entered.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();

        Action concurrentUse = () => tx.VertexExists(new VertexId(0));
        concurrentUse.Should().Throw<ConcurrentTransactionUseException>();

        op.Release.Set();
        await running;
    }

    private sealed class BlockingOperator : IPhysicalOperator
    {
        private readonly TupleSchema _schema = new([]);

        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public void Open(ITransaction tx)
        {
        }

        public bool MoveNext()
        {
            Entered.Set();
            Release.Wait();
            return false;
        }

        public TupleRef Current => new(Span<TupleSlot>.Empty);
        public TupleSchema Schema => _schema;
        public OperatorStatistics Statistics => default;

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
