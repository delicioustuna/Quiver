using System;

namespace Quiver.Storage.Records;

/// <summary>
/// FT-35 (増分2): 日時系 CLR 型と物理 Int64 (long) の正準・順序保存コーデック。
/// 格納・<c>Load</c>・クエリ述語構築 (GC-7 <c>ExpressionPredicate</c> / <c>TypedGraphTraversal.Has</c>)
/// がすべて本クラスを共有し、範囲比較の一貫性を保証する (plan の「単一コーデック」原則)。
///
/// <para><b>TimeZone 契約 (重要)</b>: DB はファイルとして別マシンに可搬であるべきなので、
/// 比較結果がマシンのローカルタイムゾーンに依存してはならない。よって:</para>
/// <list type="bullet">
///   <item><see cref="DateTimeKind.Utc"/> → そのまま UTC Ticks。</item>
///   <item><see cref="DateTimeKind.Local"/> → UTC 瞬時へ変換して格納。</item>
///   <item><see cref="DateTimeKind.Unspecified"/> → <b>UTC として扱う</b> (Local 扱いしない —
///     ローカルオフセットを当てるとマシン依存になり可搬性が壊れるため)。</item>
/// </list>
/// 復元は常に <see cref="DateTimeKind.Utc"/> の <see cref="DateTime"/> を返す (瞬時は保存される)。
/// <see cref="DateTimeOffset"/> はオフセットを絶対時刻 (<see cref="DateTimeOffset.UtcTicks"/>) に畳む。
/// </summary>
internal static class TemporalCodec
{
    /// <summary>DateTime → 正準 UTC Ticks。Local のみ UTC へ変換、Utc/Unspecified はそのまま。</summary>
    public static long ToUtcTicks(DateTime v)
        => (v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v).Ticks;

    /// <summary>UTC Ticks → <see cref="DateTimeKind.Utc"/> の DateTime。</summary>
    public static DateTime FromUtcTicks(long ticks) => new DateTime(ticks, DateTimeKind.Utc);

    /// <summary>DateTimeOffset → 絶対時刻 (UTC Ticks)。オフセットは畳まれる。</summary>
    public static long OffsetToUtcTicks(DateTimeOffset v) => v.UtcTicks;

    /// <summary>UTC Ticks → オフセット 0 (UTC) の DateTimeOffset。</summary>
    public static DateTimeOffset FromUtcTicksToOffset(long ticks) => new DateTimeOffset(ticks, TimeSpan.Zero);

    /// <summary>DateOnly → DayNumber (単調)。</summary>
    public static long ToDayNumber(DateOnly v) => v.DayNumber;

    /// <summary>DayNumber → DateOnly。</summary>
    public static DateOnly FromDayNumber(long dayNumber) => DateOnly.FromDayNumber((int)dayNumber);

    /// <summary>TimeOnly → Ticks。</summary>
    public static long ToTicks(TimeOnly v) => v.Ticks;

    /// <summary>Ticks → TimeOnly。</summary>
    public static TimeOnly ToTimeOnly(long ticks) => new TimeOnly(ticks);

    /// <summary>TimeSpan → Ticks (単調)。</summary>
    public static long ToTicks(TimeSpan v) => v.Ticks;

    /// <summary>Ticks → TimeSpan。</summary>
    public static TimeSpan ToTimeSpan(long ticks) => new TimeSpan(ticks);
}
