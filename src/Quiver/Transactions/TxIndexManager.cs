using Quiver.Core;
using Quiver.Index;
using Quiver.Index.FullText;
using Quiver.Text;

namespace Quiver.Transactions;

// 索引更新はトランザクション単位のグローバルロックで直列化する。
internal sealed class TxIndexManager : IIndexManager
{
    private readonly IIndexManager _inner;
    private readonly LockManager _locks;
    private readonly TransactionId _txId;
    private readonly TimeSpan _timeout;
    private const long GlobalIndexLockKey = long.MinValue;

    internal TxIndexManager(IIndexManager inner, LockManager locks, TransactionId txId, TimeSpan timeout)
    {
        _inner = inner; _locks = locks; _txId = txId; _timeout = timeout;
    }

    public IBTreeIndex<int> CreateInt32Index(string name) { AcquireLock(); return _inner.CreateInt32Index(name); }
    public IBTreeIndex<long> CreateInt64Index(string name) { AcquireLock(); return _inner.CreateInt64Index(name); }
    public IBTreeIndex<double> CreateDoubleIndex(string name) { AcquireLock(); return _inner.CreateDoubleIndex(name); }
    public IBTreeIndex<string> CreateStringIndex(string name) { AcquireLock(); return _inner.CreateStringIndex(name); }
    public IBTreeIndex<byte[]> CreateBytesIndex(string name) { AcquireLock(); return _inner.CreateBytesIndex(name); }
    public bool DropIndex(string name) { AcquireLock(); return _inner.DropIndex(name); }
    public IEnumerable<string> ListIndexes() => _inner.ListIndexes();

    public void RegisterIndexBinding(string indexName, string label, string propertyKey)
        => _inner.RegisterIndexBinding(indexName, label, propertyKey);

    public bool TryGetIndexName(string label, string propertyKey, out string indexName)
        => _inner.TryGetIndexName(label, propertyKey, out indexName);

    public IEnumerable<(string IndexName, string Label, string PropertyKey)> ListIndexBindings()
        => _inner.ListIndexBindings();

    // 全文索引。作成/削除は索引ロックを取得、読取は素通し。
    public FullTextIndex CreateFullTextIndex(string name, string label, string propertyKey, string tokenizerId)
    { AcquireLock(); return _inner.CreateFullTextIndex(name, label, propertyKey, tokenizerId); }

    public bool TryGetFullTextIndex(string name, out FullTextIndex index)
        => _inner.TryGetFullTextIndex(name, out index);

    public bool TryGetFullTextIndexByLabelKey(string label, string propertyKey, out FullTextIndex index)
        => _inner.TryGetFullTextIndexByLabelKey(label, propertyKey, out index);

    public IEnumerable<(string Name, string Label, string PropertyKey, string TokenizerId)> ListFullTextIndexes()
        => _inner.ListFullTextIndexes();

    public bool DropFullTextIndex(string name) { AcquireLock(); return _inner.DropFullTextIndex(name); }

    public ITokenizer ResolveTokenizer(string tokenizerId) => _inner.ResolveTokenizer(tokenizerId);
    public void RegisterTokenizer(ITokenizer tokenizer) => _inner.RegisterTokenizer(tokenizer);

    public bool HasAnyFullTextIndex => _inner.HasAnyFullTextIndex;

    public void MaintainFullText(FullTextIndex index, long entityId, string? oldText, string? newText)
    {
        AcquireLock();
        _inner.MaintainFullText(index, entityId, oldText, newText);
    }

    private void AcquireLock()
    {
        if (!_locks.TryAcquire(GlobalIndexLockKey, _txId, LockMode.Exclusive, _timeout))
            throw new TransactionException("Lock timeout acquiring index lock.");
    }
}
