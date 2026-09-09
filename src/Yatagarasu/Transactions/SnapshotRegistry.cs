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
    private readonly TimeProvider _timeProvider;
    private long _lastWarningCheck;
    private static readonly TimeSpan WarningCheckInterval = TimeSpan.FromMilliseconds(250);
    private long _nextRegistrationId;

    internal SnapshotRegistry(
        TimeSpan? warningThreshold = null,
        Action<SnapshotDiagnostics>? warningSink = null,
        TimeProvider? timeProvider = null)
    {
        _warningThreshold = warningThreshold ?? TimeSpan.FromMinutes(5);
        _warningSink = warningSink;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastWarningCheck = _timeProvider.GetTimestamp() - _timeProvider.TimestampFrequency / 4;
    }

    internal int ActiveCount => _active.Count;
    internal Action? HorizonCapturedForTest { get; set; }

    internal SnapshotDiagnostics Diagnostics
    {
        get
        {
            SnapshotEntry? oldest = null;
            foreach (SnapshotEntry entry in _active.Values)
            {
                if (oldest is null || entry.StartedAtUtc < oldest.StartedAtUtc)
                    oldest = entry;
            }

            if (oldest is null)
                return new SnapshotDiagnostics(0, TimeSpan.Zero, null, null);

            return new SnapshotDiagnostics(
                _active.Count,
                _timeProvider.GetUtcNow() - oldest.StartedAtUtc,
                oldest.StartLocation,
                oldest.CommittedHighWater);
        }
    }

    internal long OldestCommittedHighWater(long fallback)
    {
        long oldest = fallback;
        var entries = _active.Values;
        HorizonCapturedForTest?.Invoke();
        foreach (SnapshotEntry entry in entries)
            oldest = Math.Min(oldest, entry.CommittedHighWater);
        return oldest;
    }

    internal SnapshotRegistration Register(
        in SnapshotState snapshot,
        [CallerMemberName] string? startLocation = null)
        => Register(snapshot.CommittedHighWater, startLocation);

    // スナップショットの読み込み前に回収を止める。0は未確定を表し、どの版も回収対象にしない。
    internal SnapshotRegistration Reserve([CallerMemberName] string? startLocation = null)
        => Register(0, startLocation);

    private SnapshotRegistration Register(long highWater, string? startLocation)
    {
        long id = Interlocked.Increment(ref _nextRegistrationId);
        var entry = new SnapshotEntry(
            highWater,
            _timeProvider.GetUtcNow(),
            startLocation ?? "unknown");
        _active[id] = entry;
        try
        {
            ReportLeakIfNeeded();
            return new SnapshotRegistration(this, id, entry);
        }
        catch
        {
            Unregister(id);
            throw;
        }
    }

    private void ReportLeakIfNeeded()
    {
        if (_warningSink is null) return;
        long previous = Volatile.Read(ref _lastWarningCheck);
        long now = _timeProvider.GetTimestamp();
        if (_timeProvider.GetElapsedTime(previous, now) < WarningCheckInterval
            || Interlocked.CompareExchange(ref _lastWarningCheck, now, previous) != previous)
            return;

        SnapshotDiagnostics diagnostics = Diagnostics;
        if (diagnostics.OldestAge >= _warningThreshold)
            _warningSink(diagnostics);
    }

    private void Unregister(long registrationId)
        => _active.TryRemove(registrationId, out _);

    internal sealed class SnapshotEntry(long highWater, DateTimeOffset startedAtUtc, string startLocation)
    {
        private long _highWater = highWater;
        internal long CommittedHighWater => Volatile.Read(ref _highWater);
        internal DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        internal string StartLocation { get; } = startLocation;
        internal void Bind(in SnapshotState snapshot) => Volatile.Write(ref _highWater, snapshot.CommittedHighWater);
    }

    internal sealed class SnapshotRegistration : IDisposable
    {
        private SnapshotRegistry? _owner;
        private readonly long _registrationId;
        private readonly SnapshotEntry _entry;

        internal SnapshotRegistration(SnapshotRegistry owner, long registrationId, SnapshotEntry entry)
        {
            _owner = owner;
            _registrationId = registrationId;
            _entry = entry;
        }

        internal void Bind(in SnapshotState snapshot) => _entry.Bind(in snapshot);

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Unregister(_registrationId);
    }
}

internal readonly record struct SnapshotDiagnostics(
    int ActiveCount,
    TimeSpan OldestAge,
    string? OldestStartLocation,
    long? OldestCommittedHighWater);
