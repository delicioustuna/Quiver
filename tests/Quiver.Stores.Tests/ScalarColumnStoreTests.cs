using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>
/// ScalarColumnStore (永続 MVCC 列セグメント基盤) の単体テスト。
/// spike の MVCC ロジック検証 + head ページ永続 (reopen) を確認する。
/// </summary>
public class ScalarColumnStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private PagedFile _file;
    private ScalarColumnStore _col;

    public ScalarColumnStoreTests()
    {
        _file = new PagedFile(_path);
        _col = new ScalarColumnStore(_file);
    }

    public void Dispose()
    {
        _file.Dispose();
        File.Delete(_path);
    }

    private void Reopen()
    {
        _file.Dispose();
        _file = new PagedFile(_path);
        _col = new ScalarColumnStore(_file);
    }

    private static SnapshotState Snap(long snapshotTxId, params long[] active)
        => new(snapshotTxId, new HashSet<long>(active));

    private static CommittedTxRegistry Committed(params long[] txs)
    {
        var r = new CommittedTxRegistry();
        foreach (var t in txs) r.MarkCommitted(new TransactionId(t));
        return r;
    }

    [Fact]
    public void Set_and_read_visible_value()
    {
        _col.Set(0, 100, txId: 5);
        _col.Set(1, 200, txId: 5);
        var c = Committed(5);
        var snap = Snap(10);
        _col.TryGet(0, in snap, new TransactionId(10), c, out var v0).Should().BeTrue();
        v0.Should().Be(100);
        _col.ProjectSum(in snap, new TransactionId(10), c).Should().Be(300);
    }

    [Fact]
    public void Head_persists_across_reopen()
    {
        _col.Set(0, 111, txId: 5);
        _col.Set(1, 222, txId: 5);
        _col.Set(2, 333, txId: 5);

        Reopen();

        _col.Hwm.Should().Be(3);
        var c = Committed(5);
        var snap = Snap(10);
        _col.ProjectSum(in snap, new TransactionId(10), c).Should().Be(666);
        _col.TryGet(2, in snap, new TransactionId(10), c, out var v2).Should().BeTrue();
        v2.Should().Be(333);
    }

    [Fact]
    public void Many_entries_span_multiple_pages_and_reopen()
    {
        const int n = 1000; // 340/page → 複数ページ
        long expected = 0;
        for (int i = 0; i < n; i++) { _col.Set(i, i, txId: 5); expected += i; }

        Reopen();

        var c = Committed(5);
        var snap = Snap(10);
        _col.Hwm.Should().Be(n);
        _col.ProjectSum(in snap, new TransactionId(10), c).Should().Be(expected);
    }

    [Fact]
    public void Overwrite_keeps_old_version_for_prior_snapshot()
    {
        _col.Set(0, 100, txId: 5);
        _col.Set(0, 999, txId: 8);
        _col.DeltaVersionCount.Should().Be(1);

        var c = Committed(5, 8);
        _col.TryGet(0, Snap(10), new TransactionId(10), c, out var vNew).Should().BeTrue();
        vNew.Should().Be(999);
        _col.TryGet(0, Snap(7), new TransactionId(7), c, out var vOld).Should().BeTrue();
        vOld.Should().Be(100);
    }

    [Fact]
    public void Delete_hides_from_later_reader_keeps_prior()
    {
        _col.Set(0, 100, txId: 5);
        _col.Delete(0, txId: 8);
        var c = Committed(5, 8);
        // tx8 以後 → 不可視。
        _col.TryGet(0, Snap(10), new TransactionId(10), c, out _).Should().BeFalse();
        // tx8 観測前 (SnapshotTxId=7) → まだ可視。
        _col.TryGet(0, Snap(7), new TransactionId(7), c, out var v).Should().BeTrue();
        v.Should().Be(100);
    }

    [Fact]
    public void Merge_reclaims_dead_delta_below_horizon()
    {
        _col.Set(0, 100, txId: 5);
        _col.Set(0, 999, txId: 8);
        var c = Committed(8);
        _col.Merge(horizon: 9, c).Should().Be(1);
        _col.DeltaVersionCount.Should().Be(0);
        // head は残り可視。
        _col.TryGet(0, Snap(10), new TransactionId(10), Committed(8), out var v).Should().BeTrue();
        v.Should().Be(999);
    }
}
