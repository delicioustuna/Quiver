using Yatagarasu.Core;
using Yatagarasu.Telemetry;
using System.Diagnostics;

namespace Yatagarasu.Transactions;

/// <summary>
/// database instance内の書き込み所有権を一つに制限します。
/// </summary>
internal sealed class WriterLease : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly TimeSpan _timeout;
    private readonly bool _failFast;
    private long _ownerTransactionId;
    private bool _disposed;
    private readonly Action<TransactionId?>? _publishOwner;

    internal WriterLease(TimeSpan timeout, bool failFast, Action<TransactionId?>? publishOwner = null)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
        _failFast = failFast;
        _publishOwner = publishOwner;
    }

    internal TransactionId? ActiveWriterId
    {
        get
        {
            long value = Volatile.Read(ref _ownerTransactionId);
            return value == 0 ? null : new TransactionId(value);
        }
    }

    internal WriterLeaseHandle Acquire(TransactionId transactionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // timeout 付き Wait だけでは即時取得と実競合を区別できず、通常 write まで
        // contention count に混ざる。最初の非待機 probe で競合を確定してから待機時間を測る。
        bool acquired = _semaphore.Wait(0);
        if (acquired)
        {
            return SetOwner(transactionId);
        }

        YatagarasuTelemetry.WriterContentionCount.Add(1);
        YatagarasuEventSource.Log.WriterContention();
        if (_failFast)
            throw new WriterBusyException(WriterContentionMode.FailFast, TimeSpan.Zero);

        long waitStarted = Stopwatch.GetTimestamp();
        acquired = _semaphore.Wait(_timeout);
        double waitDurationMs =
            Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
        YatagarasuTelemetry.WriterWaitDurationMs.Record(waitDurationMs);
        YatagarasuEventSource.Log.WriterWaitCompleted(waitDurationMs);
        if (!acquired)
            throw new WriterBusyException(WriterContentionMode.Wait, _timeout);

        return SetOwner(transactionId);
    }

    /// <summary>
    /// 待機せずに書き込み権限を取得します。取得できない場合は例外を送出せず <c>null</c> を返します。
    /// </summary>
    internal WriterLeaseHandle? TryAcquire(TransactionId transactionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_semaphore.Wait(0)) return null;
        return SetOwner(transactionId);
    }

    private WriterLeaseHandle SetOwner(TransactionId transactionId)
    {
        try
        {
            _publishOwner?.Invoke(transactionId);
            Volatile.Write(ref _ownerTransactionId, transactionId.Value);
            return new WriterLeaseHandle(this, transactionId);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    private void Release(TransactionId transactionId)
    {
        if (Interlocked.CompareExchange(
                ref _ownerTransactionId,
                0,
                transactionId.Value) != transactionId.Value)
        {
            return;
        }
        // 次の書き込み側が権限を取得する前に、旧所有者の消去を公開する。
        try { _publishOwner?.Invoke(null); }
        finally { _semaphore.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _semaphore.Dispose();
    }

    internal sealed class WriterLeaseHandle : IDisposable
    {
        private WriterLease? _owner;
        private readonly TransactionId _transactionId;

        internal WriterLeaseHandle(WriterLease owner, TransactionId transactionId)
        {
            _owner = owner;
            _transactionId = transactionId;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Release(_transactionId);
    }
}
