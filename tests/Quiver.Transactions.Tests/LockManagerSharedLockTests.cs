using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Transactions.Tests;

/// <summary>
/// FT-24: Reader-Writer lock + wait queue + 同 tx 再入/昇格の単体テスト。
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
        await Task.Delay(50);
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
        await Task.Delay(50);

        var laterReaderTask = Task.Run(() => lm.TryAcquire(42, r2, LockMode.Shared, Short));
        await Task.Delay(50);

        // 後続 reader は writer より後ろに並ぶので、r1 が release するまで待つ。
        // 短い timeout で false。
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
        await Task.Delay(50);

        lm.Release(42, w);

        var results = await Task.WhenAll(t1, t2, t3);
        results.Should().AllBeEquivalentTo(true);
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
