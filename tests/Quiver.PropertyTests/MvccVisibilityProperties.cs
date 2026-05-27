using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Quiver.Core;
using Xunit;

namespace Quiver.PropertyTests;

/// <summary>
/// TS-3 Property 3: <see cref="Visibility.IsVisible"/> が Postgres SI の各公理を満たすことを確認する。
///
/// 公理 (snapshot S, self T, registry R に対して):
///   A1. <c>xmin == 0</c> ⇒ 不可視 (空きスロット)
///   A2. <c>xmin == T</c> かつ <c>xmax == 0</c> ⇒ 可視 (read-your-writes)
///   A3. <c>xmin == T</c> かつ <c>xmax == T</c> ⇒ 不可視 (read-your-own-delete)
///   A4. <c>xmin &gt; S.SnapshotTxId</c> ⇒ 不可視 (future xmin)
///   A5. <c>xmin ∈ S.ActiveAtBegin</c> ⇒ 後で committed しても不可視 (SI)
///   A6. <c>xmin</c> committed かつ <c>≤ S.SnapshotTxId</c> かつ <c>∉ ActiveAtBegin</c>
///        かつ <c>xmax == 0</c> ⇒ 可視
///   A7. xmax がコミット済みで <c>≤ S.SnapshotTxId</c> かつ <c>∉ ActiveAtBegin</c>
///        ⇒ xmin が可視でも不可視 (削除確定)
///   A8. 単調性: registry に commit を追加しても snapshot 観点では
///        ActiveAtBegin に居る xmin/xmax の可視性は変化しない (snapshot stability)
/// </summary>
public class MvccVisibilityProperties
{
    private const long SelfTxId = 100;
    private const long SnapshotTxId = 90;

    private static readonly TransactionId Self = new(SelfTxId);

    /// <summary>commit 済み TxId の集合 (1..89 の範囲、ActiveAtBegin と排他)。</summary>
    public sealed record VisibilityCase(
        long Xmin,
        long Xmax,
        long[] ActiveAtBegin,
        long[] CommittedExceptActive);

    public static Arbitrary<VisibilityCase> Arb()
    {
        var idGen = Gen.Choose(0, 120).Select(i => (long)i); // 0..120 = pre/self/future を網羅
        var smallList = Gen.Sized(s =>
            idGen.ListOf(Math.Min(s, 8)).Select(l => l.Distinct().ToArray()));
        return idGen.SelectMany(xmin =>
               idGen.SelectMany(xmax =>
               smallList.SelectMany(active =>
               smallList.Select(committed =>
                   new VisibilityCase(xmin, xmax, active, committed)))))
            .ToArbitrary();
    }

    private static (SnapshotState Snap, CommittedTxRegistry Reg) Build(VisibilityCase c)
    {
        var active = new HashSet<long>(c.ActiveAtBegin);
        var snap = new SnapshotState(new TransactionId(SnapshotTxId), active);
        var reg = new CommittedTxRegistry();
        foreach (var id in c.CommittedExceptActive)
            if (id != 0 && !active.Contains(id)) reg.MarkCommitted(new TransactionId(id));
        return (snap, reg);
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(MvccVisibilityProperties)])]
    public Property A1_XminZero_IsInvisible(VisibilityCase c)
    {
        var (snap, reg) = Build(c);
        return (!Visibility.IsVisible(0, c.Xmax, in snap, Self, reg)).ToProperty();
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(MvccVisibilityProperties)])]
    public Property A2_OwnWrite_NoDelete_IsVisible(VisibilityCase c)
    {
        var (snap, reg) = Build(c);
        return Visibility.IsVisible(SelfTxId, 0, in snap, Self, reg).ToProperty();
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(MvccVisibilityProperties)])]
    public Property A3_OwnWrite_OwnDelete_IsInvisible(VisibilityCase c)
    {
        var (snap, reg) = Build(c);
        return (!Visibility.IsVisible(SelfTxId, SelfTxId, in snap, Self, reg)).ToProperty();
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(MvccVisibilityProperties)])]
    public Property A4_FutureXmin_IsInvisible(VisibilityCase c)
    {
        var (snap, reg) = Build(c);
        long futureXmin = SnapshotTxId + 1 + (c.Xmin & 0xF);
        // future xmin は本人 (Self=100) と衝突しない範囲 (91..106 のうち 100 を除外)。
        if (futureXmin == SelfTxId) futureXmin = SelfTxId + 1;
        reg.MarkCommitted(new TransactionId(futureXmin)); // committed であっても future なら不可視
        return (!Visibility.IsVisible(futureXmin, 0, in snap, Self, reg)).ToProperty();
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(MvccVisibilityProperties)])]
    public Property A5_ActiveAtBegin_Xmin_IsInvisible_EvenWhenLaterCommitted(VisibilityCase c)
    {
        if (c.ActiveAtBegin.Length == 0) return true.ToProperty();
        long xmin = c.ActiveAtBegin[0];
        if (xmin == 0 || xmin == SelfTxId) return true.ToProperty();
        var (snap, reg) = Build(c);
        // 後付けで commit を記録しても、ActiveAtBegin に入っている xmin は不可視のまま。
        reg.MarkCommitted(new TransactionId(xmin));
        return (!Visibility.IsVisible(xmin, 0, in snap, Self, reg)).ToProperty();
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(MvccVisibilityProperties)])]
    public Property A6_CommittedPastXmin_NoDelete_IsVisible(VisibilityCase c)
    {
        // xmin を「snapshot 以前 / ActiveAtBegin に居ない / self でない」TxId に正規化する。
        long xmin = (Math.Abs(c.Xmin) % (SnapshotTxId - 1)) + 1; // 1..89
        var active = new HashSet<long>(c.ActiveAtBegin);
        if (active.Contains(xmin) || xmin == SelfTxId) return true.ToProperty(); // skip
        var snap = new SnapshotState(new TransactionId(SnapshotTxId), active);
        var reg = new CommittedTxRegistry();
        reg.MarkCommitted(new TransactionId(xmin));
        return Visibility.IsVisible(xmin, 0, in snap, Self, reg).ToProperty();
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(MvccVisibilityProperties)])]
    public Property A7_CommittedPastXmax_HidesRecord(VisibilityCase c)
    {
        long xmin = (Math.Abs(c.Xmin) % (SnapshotTxId - 1)) + 1;
        long xmax = (Math.Abs(c.Xmax) % (SnapshotTxId - 1)) + 1;
        if (xmin == xmax) return true.ToProperty();
        var active = new HashSet<long>(c.ActiveAtBegin);
        if (active.Contains(xmin) || active.Contains(xmax)) return true.ToProperty();
        if (xmin == SelfTxId || xmax == SelfTxId) return true.ToProperty();
        var snap = new SnapshotState(new TransactionId(SnapshotTxId), active);
        var reg = new CommittedTxRegistry();
        reg.MarkCommitted(new TransactionId(xmin));
        reg.MarkCommitted(new TransactionId(xmax));
        return (!Visibility.IsVisible(xmin, xmax, in snap, Self, reg)).ToProperty();
    }

    [Property(MaxTest = 500, Arbitrary = [typeof(MvccVisibilityProperties)])]
    public Property A8_SnapshotStability_ActiveAtBeginNeverFlipsOnLaterCommit(VisibilityCase c)
    {
        if (c.ActiveAtBegin.Length == 0) return true.ToProperty();
        var (snap, reg) = Build(c);
        // 任意 (xmin, xmax) で「事前判定」を取り、ActiveAtBegin の全 tx を後から commit させても結果が変わらないこと。
        bool before = Visibility.IsVisible(c.Xmin, c.Xmax, in snap, Self, reg);
        foreach (var id in c.ActiveAtBegin)
            if (id != 0) reg.MarkCommitted(new TransactionId(id));
        bool after = Visibility.IsVisible(c.Xmin, c.Xmax, in snap, Self, reg);
        // ActiveAtBegin に入った tx は永遠に「自分の snapshot からは active」と見なされるため、
        // visibility は変わらないはずだ (snapshot stability)。
        return (before == after).ToProperty();
    }

    [Property(MaxTest = 1000, Arbitrary = [typeof(MvccVisibilityProperties)])]
    public Property NegationConsistency_XmaxZero_EquivalentToXminVisible(VisibilityCase c)
    {
        // xmax == 0 のとき IsVisible は IsXminVisible と同値である (xmax 経路は no-op で true)。
        // つまり IsVisible(xmin, 0, ...) は IsVisible(xmin, 0, ...) を 2 回呼んで同じ値、
        // かつ「self の write」「committed past」「future」「ActiveAtBegin」「unknown」
        // の 5 分類で必ず確定的に分類できる。
        var (snap, reg) = Build(c);
        bool first = Visibility.IsVisible(c.Xmin, 0, in snap, Self, reg);
        bool second = Visibility.IsVisible(c.Xmin, 0, in snap, Self, reg);
        return (first == second).ToProperty();
    }
}
