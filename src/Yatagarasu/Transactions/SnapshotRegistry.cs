using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Yatagarasu.Core;

namespace Yatagarasu.Transactions;

/// <summary>
/// activeなreader snapshotと診断情報を保持します。
/// </summary>
internal sealed class SnapshotRegistry
{
    private readonly ConcurrentDictionary<long, SnapshotEntry> _active = new();
    private readonly TimeSpan _warningThreshold;
    private readonly Action<SnapshotDiagnostics>? _warningSink;
    private long _nextRegistrationId;

    internal SnapshotRegistry(
        TimeSpan? warningThreshold = null,
        Action<SnapshotDiagnostics>? warningSink = null)
    {
        _warningThreshold = warningThreshold ?? TimeSpan.FromMinutes(5);
        _warningSink = warningSink;
    }

    internal int ActiveCount => _active.Count;

    internal SnapshotDiagnostics Diagnostics
    {
        get
        {
            SnapshotEntry? oldest = null;
            foreach (SnapshotEntry entry in _active.Values)
            {
                if (oldest is null || entry.StartedAtUtc < oldest.Value.StartedAtUtc)
                    oldest = entry;
            }

            if (oldest is null)
                return new SnapshotDiagnostics(0, TimeSpan.Zero, null, null);

            return new SnapshotDiagnostics(
                _active.Count,
                DateTimeOffset.UtcNow - oldest.Value.StartedAtUtc,
                oldest.Value.StartLocation,
                oldest.Value.Snapshot.CommittedHighWater);
        }
    }

    internal long OldestCommittedHighWater(long fallback)
    {
        long oldest = long.MaxValue;
        foreach (SnapshotEntry entry in _active.Values)
            oldest = Math.Min(oldest, entry.Snapshot.CommittedHighWater);
        return oldest == long.MaxValue ? fallback : oldest;
    }

    internal SnapshotRegistration Register(
        in SnapshotState snapshot,
        [CallerMemberName] string? startLocation = null)
    {
        long id = Interlocked.Increment(ref _nextRegistrationId);
        _active[id] = new SnapshotEntry(
            snapshot,
            DateTimeOffset.UtcNow,
            startLocation ?? "unknown");
        ReportLeakIfNeeded();
        return new SnapshotRegistration(this, id);
    }

    private void ReportLeakIfNeeded()
    {
        if (_warningSink is null) return;
        SnapshotDiagnostics diagnostics = Diagnostics;
        if (diagnostics.OldestAge >= _warningThreshold)
            _warningSink(diagnostics);
    }

    private void Unregister(long registrationId)
        => _active.TryRemove(registrationId, out _);

    private readonly record struct SnapshotEntry(
        SnapshotState Snapshot,
        DateTimeOffset StartedAtUtc,
        string StartLocation);

    internal sealed class SnapshotRegistration : IDisposable
    {
        private SnapshotRegistry? _owner;
        private readonly long _registrationId;

        internal SnapshotRegistration(SnapshotRegistry owner, long registrationId)
        {
            _owner = owner;
            _registrationId = registrationId;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Unregister(_registrationId);
    }
}

internal readonly record struct SnapshotDiagnostics(
    int ActiveCount,
    TimeSpan OldestAge,
    string? OldestStartLocation,
    long? OldestCommittedHighWater);
