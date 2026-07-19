using Quiver.Core;

namespace Quiver.Transactions;

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

    internal WriterLease(TimeSpan timeout, bool failFast)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
        _failFast = failFast;
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
        bool acquired = _failFast
            ? _semaphore.Wait(0)
            : _semaphore.Wait(_timeout);
        if (!acquired)
        {
            if (_failFast)
                throw new WriterBusyException(WriterContentionMode.FailFast, TimeSpan.Zero);
            throw new WriterBusyException(WriterContentionMode.Wait, _timeout);
        }

        Volatile.Write(ref _ownerTransactionId, transactionId.Value);
        return new WriterLeaseHandle(this, transactionId);
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
        _semaphore.Release();
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
