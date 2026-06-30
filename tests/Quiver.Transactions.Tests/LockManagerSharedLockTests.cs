using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Transactions.Tests;

/// <summary>
/// Reader-Writer lock + wait queue + 同 tx 再入/昇格の単体テスト。
/// </summary>
public class LockManagerSharedLockTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(2);

    [Fact]
    public void Shared_lock_allows_multiple_owners()
    {
        var lm = new LockManager();
        var a = new TransactionId(1);
        var b = new TransactionId(2);
        var c = new TransactionId(3);

        lm.TryAcquire(42, a, LockMode.Shared, Short).Should().BeTrue();
        lm.TryAcquire(42, b, LockMode.Shared, Short).Should().BeTrue();
        lm.TryAcquire(42, c, LockMode.Shared, Short).Should().BeTrue();
    }

    [Fact]
    public void Exclusive_blocks_shared_until_release()
    {
        var lm = new LockManager();
        var w = new TransactionId(1);
        var r = new TransactionId(2);

        lm.TryAcquire(42, w, LockMode.Exclusive, Short).Should().BeTrue();
        // 別 tx の shared は即時取れず、短いタイムアウトで false。
        lm.TryAcquire(42, r, LockMode.Shared, Short).Should().BeFalse();

        lm.Release(42, w);
        lm.TryAcquire(42, r, LockMode.Shared, Short).Should().BeTrue();
    }

    [Fact]
    public void Shared_blocks_exclusive_until_release()
    {
        var lm = new LockManager();
        var r1 = new TransactionId(1);
        var r2 = new TransactionId(2);
        var w = new TransactionId(3);

        lm.TryAcquire(42, r1, LockMode.Shared, Short).Should().BeTrue();
        lm.TryAcquire(42, r2, LockMode.Shared, Short).Should().BeTrue();
        lm.TryAcquire(42, w, LockMode.Exclusive, Short).Should().BeFalse();

        lm.Release(42, r1);
        lm.Release(42, r2);
        lm.TryAcquire(42, w, LockMode.Exclusive, Short).Should().BeTrue();
    }

    [Fact]
    public void Same_tx_can_reacquire_exclusive()
    {
        var lm = new LockManager();
        var tx = new TransactionId(1);
        lm.TryAcquire(42, tx, LockMode.Exclusive, Short).Should().BeTrue();
        lm.TryAcquire(42, tx, LockMode.Exclusive, Short).Should().BeTrue();
        lm.TryAcquire(42, tx, LockMode.Shared, Short).Should().BeTrue();
    }

    [Fact]
    public void Same_tx_upgrades_shared_to_exclusive_when_sole_reader()
    {
        var lm = new LockManager();
        var tx = new TransactionId(1);
        lm.TryAcquire(42, tx, LockMode.Shared, Short).Should().BeTrue();
        lm.TryAcquire(42, tx, LockMode.Exclusive, Short).Should().BeTrue();
        // 他 tx の shared / exclusive はブロックされる。
        var other = new TransactionId(2);
        lm.TryAcquire(42, other, LockMode.Shared, Short).Should().BeFalse();
    }

    [Fact]
    public async Task Shared_owner_release_wakes_waiting_exclusive()
    {
        var lm = new LockManager();
        var r = new TransactionId(1);
        var w = new TransactionId(2);

        lm.TryAcquire(42, r, LockMode.Shared, Short).Should().BeTrue();

        var waitTask = Task.Run(() => lm.TryAcquire(42, w, LockMode.Exclusive, Long));
        // 固定 Task.Delay は threadpool 飽和時に writer がまだ起動しておらず flaky になるため、
        // writer が実際に wait queue へ並ぶまで決定的に待つ。
        WaitUntilEnqueued(lm, w);
        waitTask.IsCompleted.Should().BeFalse("writer should be queued behind shared reader");

        lm.Release(42, r);
        (await waitTask).Should().BeTrue();
    }

    [Fact]
    public async Task Writer_starvation_avoided_by_fifo_queue()
    {
        // 既存 reader → queue に writer → 後続 reader が来ても、writer より先に
        // grant されない (FIFO; writer starvation 回避)。
        var lm = new LockManager();
        var r1 = new TransactionId(1);
        var w = new TransactionId(2);
        var r2 = new TransactionId(3);

        lm.TryAcquire(42, r1, LockMode.Shared, Short).Should().BeTrue();

        var writerTask = Task.Run(() => lm.TryAcquire(42, w, LockMode.Exclusive, Long));
        // 固定 Task.Delay(50) だと、フル並列 (16 アセンブリ) でプロセスが CPU を奪われ writer の
        // Task.Run がまだ起動していない隙に後続 reader が FIFO をすり抜けて grant され、BeFalse が
        // 偽陽性で落ちていた。writer が確実に enqueue されるまで決定的に待ってから後続 reader を出す。
        WaitUntilEnqueued(lm, w);

        var laterReaderTask = Task.Run(() => lm.TryAcquire(42, r2, LockMode.Shared, Short));

        // 後続 reader は writer より後ろに並ぶので、r1 が release するまで grant されず短い timeout で false。
        (await laterReaderTask).Should().BeFalse();

        lm.Release(42, r1);
        (await writerTask).Should().BeTrue();
    }

    [Fact]
    public async Task Releasing_exclusive_wakes_shared_burst()
    {
        var lm = new LockManager();
        var w = new TransactionId(1);
        var r1 = new TransactionId(2);
        var r2 = new TransactionId(3);
        var r3 = new TransactionId(4);

        lm.TryAcquire(42, w, LockMode.Exclusive, Short).Should().BeTrue();

        var t1 = Task.Run(() => lm.TryAcquire(42, r1, LockMode.Shared, Long));
        var t2 = Task.Run(() => lm.TryAcquire(42, r2, LockMode.Shared, Long));
        var t3 = Task.Run(() => lm.TryAcquire(42, r3, LockMode.Shared, Long));
        // burst wake の意図 (queue 済みの shared 群を一斉に起こす) を検証するため、3 reader が
        // 確実に enqueue されてから w を release する。固定 Task.Delay だと飽和時に未 enqueue のまま
        // release され、テストの意図が形骸化する (結果は true でも burst を検証できていない)。
        WaitUntilEnqueued(lm, r1);
        WaitUntilEnqueued(lm, r2);
        WaitUntilEnqueued(lm, r3);

        lm.Release(42, w);

        var results = await Task.WhenAll(t1, t2, t3);
        results.Should().AllBeEquivalentTo(true);
    }

    /// <summary>
    /// <paramref name="waiter"/> が <see cref="LockManager"/> の wait queue に並ぶまで決定的に待つ。
    /// 固定スリープ依存だと CPU/threadpool 飽和時に <see cref="Task.Run"/> がまだ起動しておらず、
    /// 順序前提のアサーションが偽陽性で落ちる。LockManager 自身の待機エッジ状態を観測して同期する。
    /// </summary>
    private static void WaitUntilEnqueued(LockManager lm, TransactionId waiter)
    {
        var edges = new List<(TransactionId Waiter, TransactionId Holder)>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            edges.Clear();
            lm.SnapshotWaitEdges(edges);
            if (edges.Exists(e => e.Waiter == waiter)) return;
            Thread.Sleep(1);
        }
        throw new TimeoutException($"waiter tx {waiter.Value} did not enqueue within 10s");
    }

    [Fact]
    public void Lock_timeout_returns_false_without_throwing()
    {
        var lm = new LockManager();
        var holder = new TransactionId(1);
        var other = new TransactionId(2);
        lm.TryAcquire(42, holder, LockMode.Exclusive, Short).Should().BeTrue();
        lm.TryAcquire(42, other, LockMode.Exclusive, TimeSpan.FromMilliseconds(50)).Should().BeFalse();
    }

    [Fact]
    public void ReleaseAll_wakes_pending_waiters()
    {
        var lm = new LockManager();
        var a = new TransactionId(1);
        var b = new TransactionId(2);

        lm.TryAcquire(1, a, LockMode.Exclusive, Short).Should().BeTrue();
        lm.TryAcquire(2, a, LockMode.Exclusive, Short).Should().BeTrue();
        lm.ReleaseAll(a);

        lm.TryAcquire(1, b, LockMode.Exclusive, Short).Should().BeTrue();
        lm.TryAcquire(2, b, LockMode.Shared, Short).Should().BeTrue();
    }
}
