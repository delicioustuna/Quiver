using Quiver.Core;

namespace Quiver.Transactions;

internal sealed class TransactionUsageGuard(TransactionId transactionId)
{
    private int _inUse;

    internal TransactionUsageLease Enter()
    {
        if (Interlocked.CompareExchange(ref _inUse, 1, 0) != 0)
            throw new ConcurrentTransactionUseException(transactionId);
        return new TransactionUsageLease(this);
    }

    internal void Exit()
    {
        if (Interlocked.Exchange(ref _inUse, 0) == 0)
            throw new ConcurrentTransactionUseException(transactionId);
    }
}

internal readonly struct TransactionUsageLease : IDisposable
{
    private readonly LeaseState? _state;

    internal TransactionUsageLease(TransactionUsageGuard guard)
        => _state = new LeaseState(guard);

    internal TransactionUsageGuard Guard
        => _state?.Guard ?? throw new InvalidOperationException("The transaction usage lease is not initialized.");

    public void Dispose() => _state?.Dispose();

    private sealed class LeaseState : IDisposable
    {
        private TransactionUsageGuard? _guard;

        internal LeaseState(TransactionUsageGuard guard)
            => _guard = guard;

        internal TransactionUsageGuard Guard
            => _guard ?? throw new ObjectDisposedException(nameof(TransactionUsageLease));

        public void Dispose()
        {
            Interlocked.Exchange(ref _guard, null)?.Exit();
        }
    }
}
