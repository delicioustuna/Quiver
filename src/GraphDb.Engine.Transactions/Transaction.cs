using GraphDb.Engine.Core;
using GraphDb.Engine.Index;
using GraphDb.Engine.Stores;

namespace GraphDb.Engine.Transactions;

internal sealed class Transaction : ITransaction
{
    public TransactionId Id => throw new NotImplementedException();
    public IsolationLevel Level => throw new NotImplementedException();
    public long SnapshotLsn => throw new NotImplementedException();
    public TransactionState State => throw new NotImplementedException();

    public INodeStore Nodes => throw new NotImplementedException();
    public IRelationshipStore Relationships => throw new NotImplementedException();
    public IPropertyStore Properties => throw new NotImplementedException();
    public IIndexManager Indexes => throw new NotImplementedException();

    public void Commit() => throw new NotImplementedException();
    public void Abort() => throw new NotImplementedException();
    public void Dispose() { }
}
