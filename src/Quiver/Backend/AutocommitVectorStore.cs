using Quiver.Core;
using Quiver.Storage.Wal;

namespace Quiver;

/// <summary>
/// <c>db.Vectors</c> 経由のミューテーションを autocommit tx で包む <see cref="IVectorStore"/>
/// ラッパ (binary backend 専用)。
///
/// <para>スレッドに書き込み tx が既にアクティブ (<c>WalPageContext.Current != null</c>) なら、その tx へ
/// 直接書く (既存挙動の維持: ユーザ tx 内の <c>SetVector</c> はその tx と原子整合する)。tx 外で
/// 呼ばれた場合は単一の autocommit tx を張り、グラフ変更と同じ container WAL に乗せて crash-atomic に
/// 永続化する。読み取り (<see cref="KnnSearch"/> 系) と <see cref="TryGetIndex"/> は下層へ直接委譲する。</para>
///
/// <para>access methods / tx 配下 <c>SetVector</c> は生の下層ストアを使い続けるので、本ラッパは
/// 公開面 (<c>db.Vectors</c>) にのみ被さる。</para>
/// </summary>
internal sealed class AutocommitVectorStore(IVectorStore underlying, Func<IGraphTransaction> beginTx) : IVectorStore
{
    private readonly IVectorStore _underlying = underlying;
    private readonly Func<IGraphTransaction> _beginTx = beginTx;

    private void InTx(Action body)
    {
        // tx が既にアクティブなら join (二重 tx で thread-static WalPageContext を壊さない)。
        if (WalPageContext.Current is not null) { body(); return; }
        using var tx = _beginTx();
        body();
        tx.Commit();
    }

    public void CreateVectorIndex(VectorIndexSpec spec) => InTx(() => _underlying.CreateVectorIndex(spec));

    public void DropVectorIndex(string name) => InTx(() => _underlying.DropVectorIndex(name));

    public void RemoveVector(EntityKind kind, long entityId, string indexName)
        => InTx(() => _underlying.RemoveVector(kind, entityId, indexName));

    public void SetVector(EntityKind kind, long entityId, string indexName, ReadOnlySpan<float> vector)
    {
        // ReadOnlySpan はラムダに捕捉できないため InTx を展開する。
        if (WalPageContext.Current is not null)
        {
            _underlying.SetVector(kind, entityId, indexName, vector);
            return;
        }
        using var tx = _beginTx();
        _underlying.SetVector(kind, entityId, indexName, vector);
        tx.Commit();
    }

    public IReadOnlyList<VectorIndexSpec> ListVectorIndexes() => _underlying.ListVectorIndexes();

    public bool TryGetIndex(string name, out VectorIndexSpec spec) => _underlying.TryGetIndex(name, out spec);

    public bool TryGetVector(EntityKind kind, long entityId, string indexName, Span<float> destination)
        => _underlying.TryGetVector(kind, entityId, indexName, destination);

    public VectorSearchCursor KnnSearch(string indexName, ReadOnlySpan<float> query, int k)
        => _underlying.KnnSearch(indexName, query, k);

    internal VectorSearchCursor KnnSearchExact(
        string indexName, ReadOnlySpan<float> query, int k)
        => _underlying is Storage.Records.PersistentVectorStore persistent
            ? persistent.KnnSearchExact(indexName, query, k)
            : throw new InvalidOperationException(
                "Exact persistent KNN baseline is available only for the binary backend.");

    public IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        string indexName, IReadOnlyList<ReadOnlyMemory<float>> queries, int k)
        => _underlying.KnnSearchBatch(indexName, queries, k);
}
