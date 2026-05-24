using FluentAssertions;
using Quiver.Core;
using Xunit;

namespace Quiver.Transactions.Tests;

/// <summary>
/// FT-26: <see cref="Visibility.IsVisible"/> 述語の単体テスト。
/// snapshot / committed registry / self TxId の各組み合わせで Postgres SI 風の判定が出ることを確認する。
/// </summary>
public class VisibilityTests
{
    private static readonly TransactionId Self = new(10);
    private static readonly TransactionId Snapshot = new(20);

    private static SnapshotState MakeSnapshot(params long[] activeAtBegin)
        => new(Snapshot, new HashSet<long>(activeAtBegin));

    private static CommittedTxRegistry RegistryWith(params long[] committedTxIds)
    {
        var reg = new CommittedTxRegistry();
        foreach (var id in committedTxIds) reg.MarkCommitted(new TransactionId(id));
        return reg;
    }

    [Fact]
    public void Xmin_zero_means_uninitialised_and_invisible()
    {
        var snap = MakeSnapshot();
        var reg = RegistryWith();
        Visibility.IsVisible(xmin: 0, xmax: 0, in snap, Self, reg).Should().BeFalse();
    }

    [Fact]
    public void Own_writes_are_visible_to_self()
    {
        var snap = MakeSnapshot();
        var reg = RegistryWith();
        // 自分の Tx の書き込みは committed 前でも visible (read-your-writes)。
        Visibility.IsVisible(xmin: Self.Value, xmax: 0, in snap, Self, reg).Should().BeTrue();
    }

    [Fact]
    public void Own_delete_hides_own_record_from_self()
    {
        var snap = MakeSnapshot();
        var reg = RegistryWith();
        Visibility.IsVisible(xmin: Self.Value, xmax: Self.Value, in snap, Self, reg).Should().BeFalse();
    }

    [Fact]
    public void Committed_xmin_before_snapshot_is_visible()
    {
        var snap = MakeSnapshot();
        var reg = RegistryWith(committedTxIds: 5);
        Visibility.IsVisible(xmin: 5, xmax: 0, in snap, Self, reg).Should().BeTrue();
    }

    [Fact]
    public void Future_xmin_after_snapshot_is_invisible()
    {
        var snap = MakeSnapshot();
        var reg = RegistryWith(committedTxIds: 30); // 30 > Snapshot(20)
        Visibility.IsVisible(xmin: 30, xmax: 0, in snap, Self, reg).Should().BeFalse();
    }

    [Fact]
    public void Active_at_begin_xmin_is_invisible_even_when_later_committed()
    {
        // 開始時に並行 active だった tx (=7) はその後 committed しても snapshot から見えない (SI)。
        var snap = MakeSnapshot(activeAtBegin: 7);
        var reg = RegistryWith(committedTxIds: 7);
        Visibility.IsVisible(xmin: 7, xmax: 0, in snap, Self, reg).Should().BeFalse();
    }

    [Fact]
    public void Aborted_xmin_is_invisible()
    {
        var snap = MakeSnapshot();
        var reg = RegistryWith(); // 8 はどこにも登録されていない = aborted / 不明
        Visibility.IsVisible(xmin: 8, xmax: 0, in snap, Self, reg).Should().BeFalse();
    }

    [Fact]
    public void Committed_xmax_before_snapshot_hides_record()
    {
        var snap = MakeSnapshot();
        var reg = RegistryWith(committedTxIds: new[] { 5L, 9L });
        Visibility.IsVisible(xmin: 5, xmax: 9, in snap, Self, reg).Should().BeFalse();
    }

    [Fact]
    public void Aborted_xmax_does_not_hide_record()
    {
        var snap = MakeSnapshot();
        var reg = RegistryWith(committedTxIds: 5); // xmax=9 は未 commit → 削除無効
        Visibility.IsVisible(xmin: 5, xmax: 9, in snap, Self, reg).Should().BeTrue();
    }

    [Fact]
    public void Future_xmax_after_snapshot_does_not_hide_record()
    {
        var snap = MakeSnapshot();
        var reg = RegistryWith(committedTxIds: new[] { 5L, 30L });
        // xmax=30 > Snapshot(20): 自分の snapshot 以後の削除なので無視する。
        Visibility.IsVisible(xmin: 5, xmax: 30, in snap, Self, reg).Should().BeTrue();
    }

    [Fact]
    public void Active_at_begin_xmax_does_not_hide_record()
    {
        var snap = MakeSnapshot(activeAtBegin: 9);
        var reg = RegistryWith(committedTxIds: new[] { 5L, 9L });
        // xmax=9 は ActiveAtBegin に含まれる → 削除はまだ snapshot 時点で未確定。
        Visibility.IsVisible(xmin: 5, xmax: 9, in snap, Self, reg).Should().BeTrue();
    }

    [Fact]
    public void Bootstrap_xmin_visible_via_registry_default()
    {
        // FT-26: CommittedTxRegistry は ctor で Bootstrap を committed として登録する。
        var snap = MakeSnapshot();
        var reg = new CommittedTxRegistry();
        Visibility.IsVisible(xmin: TransactionId.Bootstrap.Value, xmax: 0, in snap, Self, reg)
            .Should().BeTrue();
    }

    [Fact]
    public void MvccContext_ambient_overload_uses_thread_static_state()
    {
        // ambient コンテキスト未設定: committed registry が null なので xmin != 0 && xmax == 0 で visible。
        Visibility.IsVisibleAmbient(xmin: 100, xmax: 0).Should().BeTrue();
        Visibility.IsVisibleAmbient(xmin: 0, xmax: 0).Should().BeFalse();
        Visibility.IsVisibleAmbient(xmin: 100, xmax: 5).Should().BeFalse();
    }

    [Fact]
    public void CommittedTxRegistry_prune_keeps_bootstrap()
    {
        var reg = new CommittedTxRegistry();
        reg.MarkCommitted(new TransactionId(5));
        reg.MarkCommitted(new TransactionId(7));
        int removed = reg.PruneBelow(horizonTxId: 10);
        removed.Should().Be(2);
        reg.IsCommitted(TransactionId.Bootstrap.Value).Should().BeTrue();
        reg.IsCommitted(5).Should().BeFalse();
    }
}
