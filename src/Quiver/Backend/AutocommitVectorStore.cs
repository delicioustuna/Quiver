using Quiver.Core;

namespace Quiver;

/// <summary>
/// <c>db.Vectors</c> 経由のミューテーションを autocommit tx で包む <see cref="IVectorStore"/>
/// ラッパ (binary backend 専用)。
///
/// <para>各 mutation は単一の autocommit tx を張り、グラフ変更と同じ container WAL に乗せて crash-atomic に
/// 永続化する。読み取り (<see cref="KnnSearch"/> 系) と <see cref="TryGetIndex"/> は下層へ直接委譲する。</para>
///
/// <para>access methods / tx 配下 <c>SetVector</c> は生の下層ストアを使い続けるので、本ラッパは
/// 公開面 (<c>db.Vectors</c>) にのみ被さる。</para>
/// </summary>
internal sealed class AutocommitVectorStore : IVectorStore
{
    private readonly IVectorStore _underlying;
    private readonly Func<IGraphTransaction> _beginTx;

    public AutocommitVectorStore(IVectorStore underlying, Func<IGraphTransaction> beginTx)
    {
        ArgumentNullException.ThrowIfNull(underlying);
        ArgumentNullException.ThrowIfNull(beginTx);

        // QuiverDatabase と backend の双方が公開面を保護するため、二重ラップされる場合がある。
        // mutation body は必ず最外層が開始した transaction の raw store に委譲し、
        // 内側の autocommit が writer lease を再取得しないよう平坦化する。
        _underlying = underlying is AutocommitVectorStore nested
            ? nested._underlying
            : underlying;
        _beginTx = beginTx;
    }

    private void InTx(Action body)
    {
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
        using var tx = _beginTx();
        _underlying.SetVector(kind, entityId, indexName, vector);
        tx.Commit();
    }

    public IReadOnlyList<VectorIndexSpec> ListVectorIndexes() => _underlying.ListVectorIndexes();

    public bool TryGetIndex(string name, out VectorIndexSpec spec) => _underlying.TryGetIndex(name, out spec);

    public bool TryGetVector(EntityKind kind, long entityId, string indexName, Span<float> destination)
        => _underlying.TryGetVector(kind, entityId, indexName, destination);

    public VectorSearchCursor KnnSearch(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options = null)
        => _underlying.KnnSearch(indexName, query, k, options);

    internal VectorSearchCursor KnnSearchExact(
        string indexName, ReadOnlySpan<float> query, int k)
        => _underlying switch
        {
            Storage.Records.PersistentVectorStore persistent =>
                persistent.KnnSearchExact(indexName, query, k),
            AutocommitVectorStore nested =>
                nested.KnnSearchExact(indexName, query, k),
            _ => throw new InvalidOperationException(
                "Exact persistent KNN baseline is available only for the binary backend."),
        };

    public IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        string indexName,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        int k,
        VectorSearchOptions? options = null)
        => _underlying.KnnSearchBatch(indexName, queries, k, options);
}
