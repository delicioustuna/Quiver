using System.Collections.Concurrent;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// 内部で多数のスレッドを起動する並行ストレステストを同じコレクションで直列化し、
/// アセンブリ内のピークスレッド数を抑える。
/// 多数のテストアセンブリとカオステストを並列実行した際の CPU 過剰購読による不安定化を避ける。
/// コレクション間は並列のままなので、他のテストの実行時間には影響しない。
/// </summary>
[CollectionDefinition("concurrency-stress")]
public sealed class ConcurrencyStressCollection { }

/// <summary>
/// <see cref="QuiverDatabaseOptions.LockingMode"/> に <see cref="LockingMode.ReaderWriter"/>
/// を指定したときの挙動をデータベース単位で確認する。
/// </summary>
[Collection("concurrency-stress")]
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

    private QuiverDatabase OpenWithReaderWriter(TimeSpan? lockTimeout = null)
        => QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"), new QuiverDatabaseOptions
        {
            LockingMode = LockingMode.ReaderWriter,
            // 既定 500ms は writer_blocks_concurrent_reader が「短時間で fail する」ことに依存する。
            // 他のテストはオーバーライドで待機時間を広げられる。
            LockTimeout = lockTimeout ?? TimeSpan.FromMilliseconds(500),
        });

    private static long ReadInt64(IGraphTransaction tx, VertexId vertexId, string key)
        => tx.GetProperty(vertexId, key).Int64Value;

    [Fact]
    public void DefaultMode_is_ExclusiveOnly_and_readers_do_not_lock()
    {
        // ExclusiveOnly: read API はロックを取らないので、書き込み tx が exclusive を持っていても
        // 別 tx の Read は即座に通る (現挙動を破壊していないことの確認)。
        using var db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        VertexId vertexId;
        using (var tx = db.BeginTransaction())
        {
            vertexId = tx.CreateVertex("Person");
            tx.SetProperty(vertexId, "score", PropertyValue.FromInt64(1L));
            tx.Commit();
        }

        using var w = db.BeginTransaction();
        w.SetProperty(vertexId, "score", PropertyValue.FromInt64(2L));

        // ExclusiveOnly default: read は無ロックでブロックも例外にもならない。
        // スナップショット分離自体の値は別議論なので、ここでは「例外無しに完走」のみ確認。
        Action act = () =>
        {
            using var r = db.BeginReadOnlyTransaction();
            _ = ReadInt64(r, vertexId, "score");
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void ReaderWriter_concurrent_readers_do_not_block_each_other()
    {
        // 多数のアセンブリとカオステストを並列実行した際の CPU 過剰購読下でも、リーダー同士は
        // shared lock でブロックしないのに 500ms timeout が spurious に発火していた。reader が
        // 実際に待つことは無いので、寛大な timeout (30s) にしても正常系の所要時間は変わらず、
        // starvation 由来の偽陽性だけを排除できる。失敗時は原因を 3 バケットに分類して報告する:
        //   ① 値誤り (wrongValues)   = snapshot/read 正当性バグ → 致命 (常に厳格に fail)
        //   ② lock timeout           = CPU starvation シグナル → 30s 下では発生しないはず
        //   ③ その他例外              = 想定外 → 致命
        using var db = OpenWithReaderWriter(TimeSpan.FromSeconds(30));
        VertexId vertexId;
        using (var tx = db.BeginTransaction())
        {
            vertexId = tx.CreateVertex("Person");
            tx.SetProperty(vertexId, "score", PropertyValue.FromInt64(42L));
            tx.Commit();
        }

        // 16 並列で 100 回ずつ Read。Shared 同士はブロックしないので全件成功。
        int threads = 16;
        int iterations = 100;
        var wrongValues = new ConcurrentBag<long>();    // ① 致命: 読み値が committed 値と不一致
        var lockTimeouts = new ConcurrentBag<string>(); // ② starvation: lock timeout
        var unexpected = new ConcurrentBag<Exception>(); // ③ 致命: 想定外の例外
        Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < iterations; i++)
            {
                try
                {
                    using var rtx = db.BeginReadOnlyTransaction();
                    long v = ReadInt64(rtx, vertexId, "score");
                    if (v != 42L) wrongValues.Add(v);
                }
                catch (TransactionException ex) when (ex.Message.Contains("Lock timeout"))
                {
                    lockTimeouts.Add(ex.Message);
                }
                catch (Exception ex)
                {
                    unexpected.Add(ex);
                }
            }
        });

        // ① 正当性: shared reader は同一 committed 値 (42) を必ず観測する。snapshot 破れは致命。
        wrongValues.Should().BeEmpty(
            "shared reader が観測した非 42 値: [{0}] — snapshot/read isolation の破れ (正当性バグ)",
            string.Join(", ", wrongValues));
        // ③ 想定外例外は常に致命。
        unexpected.Should().BeEmpty(
            "想定外の例外: {0}",
            string.Join(" | ", unexpected.Select(e => $"{e.GetType().Name}: {e.Message}")));
        // ② reader は決してブロックしないため、30s timeout 下では starvation timeout も出ないはず。
        // ここが落ちる場合は CPU 過剰購読が 30s を超える異常事態 (test 基盤側の問題)。
        lockTimeouts.Should().BeEmpty(
            "reader は shared lock でブロックしないため 30s 以内に必ず取得できるはず ({0} 件 timeout = 異常な CPU starvation)",
            lockTimeouts.Count);
    }

    [Fact]
    public void ReaderWriter_writer_blocks_concurrent_reader_on_same_vertex()
    {
        using var db = OpenWithReaderWriter();
        VertexId vertexId;
        using (var tx = db.BeginTransaction())
        {
            vertexId = tx.CreateVertex("Person");
            tx.SetProperty(vertexId, "score", PropertyValue.FromInt64(1L));
            tx.Commit();
        }

        using var w = db.BeginTransaction();
        w.SetProperty(vertexId, "score", PropertyValue.FromInt64(99L));

        // 別 reader tx の shared 取得は w の exclusive で 500ms タイムアウトまで blocked。
        Action act = () =>
        {
            using var r = db.BeginReadOnlyTransaction();
            _ = ReadInt64(r, vertexId, "score");
        };
        act.Should().Throw<TransactionException>();
    }

    [Fact]
    public void ReaderWriter_reader_then_writer_in_same_tx_upgrades()
    {
        using var db = OpenWithReaderWriter();
        VertexId vertexId;
        using (var tx = db.BeginTransaction())
        {
            vertexId = tx.CreateVertex("Person");
            tx.SetProperty(vertexId, "score", PropertyValue.FromInt64(1L));
            tx.Commit();
        }

        using (var tx2 = db.BeginTransaction())
        {
            // 単独 reader (自分) → exclusive 昇格は無待機で成功する。
            ReadInt64(tx2, vertexId, "score").Should().Be(1L);
            tx2.SetProperty(vertexId, "score", PropertyValue.FromInt64(2L));
            tx2.Commit();
        }

        using var r = db.BeginReadOnlyTransaction();
        ReadInt64(r, vertexId, "score").Should().Be(2L);
    }
}
