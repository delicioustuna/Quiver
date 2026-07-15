namespace Quiver.Transactions;

internal readonly struct TransactionUsageLease : IDisposable
{
    private readonly Transaction? _transaction;

    internal TransactionUsageLease(Transaction transaction)
        => _transaction = transaction;

    public void Dispose()
        => _transaction?.ExitUsage();
}
