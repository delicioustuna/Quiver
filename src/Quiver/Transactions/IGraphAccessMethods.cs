using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Transactions;

/// <summary>
/// バックエンドの access methods コントラクト。
/// オペレータは <see cref="ITransaction.Vertices"/> / <see cref="ITransaction.Edges"/> /
/// <see cref="ITransaction.AdjacencySegments"/> を直接叩く代わりに、scan / seek / expand を
/// このインタフェースを経由してルーティングする。これにより各バックエンドは独自の access path
/// (リンクリスト、隣接ブロック、Edgeスキャン等) を選択できる。
/// </summary>
internal interface IGraphAccessMethods
{
    /// <summary>生存中のVertexを列挙する。任意でラベル 1 件に絞り込める。</summary>
    IEnumerable<VertexId> ScanVertices(ITransaction tx, LabelId? label = null);

    /// <summary>
    /// ラベル絞り込みが O(|L|) で走るかを backend が申告する capability。
    /// バイナリ backend は <c>LabelVertexIndex</c> sidecar が接続されているとき <c>true</c>。
    /// <c>InlineGraphAccessMethods</c> / 単体テストや ANN bypass 等 sidecar の無い backend では <c>false</c>。
    /// optimizer (PendingKnnBuilder の push-down 閾値) が graph-first / vector-first の選択に用いる。
    /// </summary>
    bool HasFastLabelIndex => false;

    /// <summary>
    /// 登録済みベクトルインデックスの内部 descriptor を取得する。
    /// PendingKnnBuilder が dim を取得して dim-aware piecewise threshold を引くのに使う。
    /// 既定実装は <c>false</c> — ベクトルメタを expose しない backend では dim awareness を無効化し、
    /// 単一閾値経路にフォールバックする。
    /// </summary>
    bool TryGetVectorIndex(
        string indexName,
        out VectorIndexDescriptor descriptor)
    {
        descriptor = default!;
        return false;
    }

    /// <summary>
    /// B+Tree インデックスを完全一致でシークする。<paramref name="key"/> の
    /// <see cref="PropertyValue.Type"/> に基づき型ごとのインデックスへルーティングする。
    /// インデックスが存在しないか、型が未対応の場合は空シーケンスを返す。
    /// </summary>
    IEnumerable<VertexId> SeekVerticesByIndex(
        ITransaction tx,
        ScalarIndexDefinition definition,
        PropertyKeyId propertyKey,
        LabelId? scope,
        PropertyValue key);

    /// <summary>
    /// <paramref name="source"/> に接続するエッジのうち、要求された方向と任意の型フィルタに
    /// マッチするものを列挙するカーソルを開く。アクセス経路の選択 (バイナリバックエンドでは
    /// 隣接ブロック + リンクリストフォールバック) はカーソルが管理し、低速経路に落ちる度に
    /// <see cref="AdjacencyFallbackCount"/> を増やす。
    /// </summary>
    ExpandCursor Expand(
        ITransaction tx,
        VertexId source,
        Direction direction,
        EdgeTypeId? typeFilter);

    /// <summary>
    /// <see cref="Expand"/> が放出するエッジ数の推定値。オプティマイザのプラン選択で
    /// バッファサイズ / ストラテジ選定に使われる。
    /// </summary>
    double EstimateExpandCardinality(
        ITransaction tx,
        VertexId source,
        Direction direction,
        EdgeTypeId? typeFilter);

    /// <summary>
    /// expand カーソルが fast path (例: 隣接ブロックバッファが満杯になった場合) を諦めて
    /// リンクリストウォークにフォールバックした累計回数。<c>IDiagnosticsApi.GetStatistics</c>
    /// 経由で公開される。
    /// </summary>
    long AdjacencyFallbackCount { get; }

    /// <summary>
    /// 指定エンティティの格納ベクトルを <paramref name="destination"/> へ読み出す。
    /// alloc-free — 呼び出し側がインデックスの次元数以上のバッファを用意する。
    /// 未設定 / 削除済み / 世代不一致は <c>false</c>。
    /// </summary>
    bool TryGetVector(
        ITransaction transaction,
        EntityRef owner,
        string indexName,
        Span<float> destination)
        => false;

    /// <summary>
    /// KNN access path。オペレータがベクトル検索をファーストクラスの
    /// スキャンソースとして扱えるようにする。
    /// query スパンは内部でコピーするので、呼び出し側が呼び出し以降も保持する必要はない。
    /// </summary>
    /// <remarks>
    /// Cosine / Dot では類似度降順、Euclidean では距離昇順 (内部で
    /// 符号反転して "高いほど近い" スコアに揃える) で結果を返す。
    /// ベクトルストアを持たないバックエンドは <see cref="NotSupportedException"/> を投げる。
    /// </remarks>
    VectorSearchCursor KnnSearch(
        ITransaction transaction,
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        VectorSearchOptions? options = null)
        => throw new NotSupportedException(
            "このバックエンドはtransaction-scoped KNNを実装していません。");

    /// <summary>
    /// 同一インデックスに対する複数クエリを 1 回の呼び出しで投げる access path。
    /// 既定実装は <see cref="KnnSearch"/> を Q 回呼ぶフォールバック。in-memory backend は
    /// 単一 snapshot で Q×N をスコアリングするオーバーライドを提供する。
    /// </summary>
    IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        ITransaction transaction,
        string indexName,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        int k,
        VectorSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var arr = new VectorSearchCursor[queries.Count];
        for (int i = 0; i < queries.Count; i++)
            arr[i] = KnnSearch(transaction, indexName, queries[i].Span, k, options);
        return arr;
    }

    /// <summary>
    /// フィルタ付き KNN。<paramref name="candidates"/> のメンバーに限定して
    /// 上位 <paramref name="k"/> 件のベクトルを返す。既定実装は <see cref="KnnSearch"/> を
    /// オーバーサンプリング (k → 2k → 4k …) してポストフィルタを掛け、k 件揃うか
    /// オーバーサンプル上限に達するまで繰り返す。ANN 構造がサポートしていれば、
    /// バックエンドはベクトルインデックス側にフィルタを push down してよい。
    /// </summary>
    /// <remarks>
    /// スコア順序は維持され、類似度降順で返る。
    /// <paramref name="candidates"/> の owner kind がインデックスの
    /// <see cref="EntityKind"/> と異なる場合は常に一致無しになる。これは構成誤りで、
    /// オペレータ層が顕在化させる責務であり、本契約違反ではない。
    /// </remarks>
    VectorSearchCursor KnnSearchFiltered(
        ITransaction transaction,
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        IReadOnlySet<EntityRef> candidates,
        VectorSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            return EmptyVectorSearchCursor.Instance;
        var hits = new List<VectorSearchResult>(k);
        using (var cursor = KnnSearch(
                   transaction,
                   indexName,
                   query,
                   Math.Max(k, candidates.Count),
                   options))
        {
            while (cursor.MoveNext())
            {
                VectorSearchResult hit = cursor.Current;
                if (!candidates.Contains(hit.Owner))
                    continue;
                hits.Add(hit);
                if (hits.Count >= k)
                    break;
            }
        }
        return new MaterializedVectorSearchCursor(hits);
    }
}

internal sealed class EmptyVectorSearchCursor : VectorSearchCursor
{
    public static readonly EmptyVectorSearchCursor Instance = new();
    public override bool MoveNext() => false;
    public override VectorSearchResult Current
        => throw new InvalidOperationException("カーソルは空です。");
}

internal sealed class MaterializedVectorSearchCursor(IReadOnlyList<VectorSearchResult> hits) : VectorSearchCursor
{
    private int _i = -1;
    public override bool MoveNext() => ++_i < hits.Count;
    public override VectorSearchResult Current => hits[_i];
}

/// <summary>
/// 1 ホップ展開のためにバックエンドが保持するカーソル。以前 <c>ExpandOperator</c> /
/// <c>BfsOperator</c> 内にインラインで存在していた「隣接ブロックかリンクリストか」の
/// 場合分けを置き換える。
/// </summary>
internal abstract class ExpandCursor : IDisposable
{
    /// <summary>次のエッジに進む。エッジを使い切ったら false を返す。</summary>
    public abstract bool MoveNext();

    /// <summary>現在エッジの隣接Vertex ID。</summary>
    public abstract VertexId Neighbor { get; }

    /// <summary>現在エッジのEdge ID。</summary>
    public abstract EdgeId Edge { get; }

    /// <summary>
    /// 現在エッジの生 64 ビット payload (典型的にはエッジ重み)。adjacency segment 裏付けの
    /// カーソルは inline payload lane を転送する。それ以外のカーソルは 0 を返す。
    /// 有効な payload 種別が Double のときは <see cref="BitConverter.Int64BitsToDouble"/> で
    /// <see cref="double"/> として再解釈する。
    /// </summary>
    public virtual long WeightRaw => 0;

    /// <inheritdoc/>
    public virtual void Dispose() { }
}
