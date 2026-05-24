using FluentAssertions;
using Quiver.Core;
using Quiver.Stores;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// FT-24: <see cref="GraphDatabaseOptions.LockingMode"/> = <see cref="LockingMode.ReaderWriter"/>
/// 時の挙動を database レベルで確認する。
/// </summary>
public sealed class ReaderWriterLockingModeTests : IDisposable
{
    private readonly string _dir;

    public ReaderWriterLockingModeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_ft24_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private GraphDatabase OpenWithReaderWriter()
        => GraphDatabase.Open(_dir, new GraphDatabaseOptions
        {
            LockingMode = LockingMode.ReaderWriter,
            LockTimeout = TimeSpan.FromMilliseconds(500),
        });

    private static long ReadInt64(IGraphTransaction tx, NodeId nodeId, string key)
        => tx.GetProperty(nodeId, key).Int64Value;

    [Fact]
    public void DefaultMode_is_ExclusiveOnly_and_readers_do_not_lock()
    {
        // ExclusiveOnly: read API はロックを取らないので、書き込み tx が exclusive を持っていても
        // 別 tx の Read は即座に通る (現挙動を破壊していないことの確認)。
        using var db = GraphDatabase.Open(_dir);
        NodeId nodeId;
        using (var tx = db.BeginTransaction())
        {
            nodeId = tx.CreateNode("Person");
            tx.SetProperty(nodeId, "score", PropertyValue.FromInt64(1L));
            tx.Commit();
        }

        using var w = db.BeginTransaction();
        w.SetProperty(nodeId, "score", PropertyValue.FromInt64(2L));

        // ExclusiveOnly default: read は無ロックでブロックも例外にもならない。
        // スナップショット分離自体の値は別議論なので、ここでは「例外無しに完走」のみ確認。
        Action act = () =>
        {
            using var r = db.BeginReadOnlyTransaction();
            _ = ReadInt64(r, nodeId, "score");
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void ReaderWriter_concurrent_readers_do_not_block_each_other()
    {
        using var db = OpenWithReaderWriter();
        NodeId nodeId;
        using (var tx = db.BeginTransaction())
        {
            nodeId = tx.CreateNode("Person");
            tx.SetProperty(nodeId, "score", PropertyValue.FromInt64(42L));
            tx.Commit();
        }

        // 16 並列で 100 回ずつ Read。Shared 同士はブロックしないので全件成功。
        int threads = 16;
        int iterations = 100;
        int errors = 0;
        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < iterations; i++)
            {
                try
                {
                    using var rtx = db.BeginReadOnlyTransaction();
                    if (ReadInt64(rtx, nodeId, "score") != 42L)
                        Interlocked.Increment(ref errors);
                }
                catch { Interlocked.Increment(ref errors); }
            }
        });
        errors.Should().Be(0);
    }

    [Fact]
    public void ReaderWriter_writer_blocks_concurrent_reader_on_same_node()
    {
        using var db = OpenWithReaderWriter();
        NodeId nodeId;
        using (var tx = db.BeginTransaction())
        {
            nodeId = tx.CreateNode("Person");
            tx.SetProperty(nodeId, "score", PropertyValue.FromInt64(1L));
            tx.Commit();
        }

        using var w = db.BeginTransaction();
        w.SetProperty(nodeId, "score", PropertyValue.FromInt64(99L));

        // 別 reader tx の shared 取得は w の exclusive で 500ms タイムアウトまで blocked。
        Action act = () =>
        {
            using var r = db.BeginReadOnlyTransaction();
            _ = ReadInt64(r, nodeId, "score");
        };
        act.Should().Throw<TransactionException>();
    }

    [Fact]
    public void ReaderWriter_reader_then_writer_in_same_tx_upgrades()
    {
        using var db = OpenWithReaderWriter();
        NodeId nodeId;
        using (var tx = db.BeginTransaction())
        {
            nodeId = tx.CreateNode("Person");
            tx.SetProperty(nodeId, "score", PropertyValue.FromInt64(1L));
            tx.Commit();
        }

        using (var tx2 = db.BeginTransaction())
        {
            // 単独 reader (自分) → exclusive 昇格は無待機で成功する。
            ReadInt64(tx2, nodeId, "score").Should().Be(1L);
            tx2.SetProperty(nodeId, "score", PropertyValue.FromInt64(2L));
            tx2.Commit();
        }

        using var r = db.BeginReadOnlyTransaction();
        ReadInt64(r, nodeId, "score").Should().Be(2L);
    }
}
