namespace Yatagarasu.Maintenance;

/// <summary>処理段階の途中では中断せず、次の段階の開始だけを制限する単調時計。</summary>
internal sealed class VacuumBudget(int maximumMilliseconds, TimeProvider timeProvider)
{
    private readonly long _started = timeProvider.GetTimestamp();
    private bool _expired;

    internal long ElapsedMilliseconds => (long)timeProvider.GetElapsedTime(_started).TotalMilliseconds;

    internal bool CanStartPhase()
    {
        if (_expired) return false;
        if (maximumMilliseconds <= 0) return true;
        _expired = ElapsedMilliseconds >= maximumMilliseconds;
        return !_expired;
    }
}
