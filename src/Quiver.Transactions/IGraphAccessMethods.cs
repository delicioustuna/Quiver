using Quiver.Core;
using Quiver.Stores;

namespace Quiver.Transactions;

/// <summary>
/// バックエンドの access methods コントラクト (BA-3、codex_advice_3.md 1 節)。
/// オペレータは <see cref="ITransaction.Nodes"/> / <see cref="ITransaction.Relationships"/> /
/// <see cref="ITransaction.AdjacencyBlocks"/> を直接叩く代わりに、scan / seek / expand を
/// このインタフェースを経由してルーティングする。これにより各バックエンドは独自の access path
/// (リンクリスト、隣接ブロック、リレーションシップスキャン等) を選択できる。
/// </summary>
public interface IGraphAccessMethods
{
    /// <summary>生存中のノードを列挙する。任意でラベル 1 件に絞り込める。</summary>
    IEnumerable<NodeId> ScanNodes(ITransaction tx, LabelId? label = null);

    /// <summary>
    /// B+Tree インデックスを完全一致でシークする。<paramref name="key"/> の
    /// <see cref="PropertyValue.Type"/> に基づき型ごとのインデックスへルーティングする。
    /// インデックスが存在しないか、型が未対応の場合は空シーケンスを返す。
    /// </summary>
    IEnumerable<NodeId> SeekNodesByIndex(ITransaction tx, string indexName, PropertyValue key);

    /// <summary>
    /// <paramref name="source"/> に接続するエッジのうち、要求された方向と任意の型フィルタに
    /// マッチするものを列挙するカーソルを開く。アクセス経路の選択 (バイナリバックエンドでは
    /// 隣接ブロック + リンクリストフォールバック) はカーソルが管理し、低速経路に落ちる度に
    /// <see cref="AdjacencyFallbackCount"/> を増やす。
    /// </summary>
    ExpandCursor Expand(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter);

    /// <summary>
    /// <see cref="Expand"/> が放出するエッジ数の推定値。オプティマイザのプラン選択で
    /// バッファサイズ / ストラテジ選定に使われる。
    /// </summary>
    double EstimateExpandCardinality(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter);

    /// <summary>
    /// expand カーソルが fast path (例: 隣接ブロックバッファが満杯になった場合) を諦めて
    /// リンクリストウォークにフォールバックした累計回数。<c>IDiagnosticsApi.GetStatistics</c>
    /// 経由で公開される。
    /// </summary>
    long AdjacencyFallbackCount { get; }

    /// <summary>
    /// VEC-5: KNN access path。バックエンドの <see cref="IVectorStore"/> に委譲し、
    /// オペレータがベクトル検索をファーストクラスのスキャンソースとして扱えるようにする。
    /// query スパンは内部でコピーするので、呼び出し側が呼び出し以降も保持する必要はない。
    /// </summary>
    /// <remarks>
    /// codex_advice_3.md 6.4 節。Cosine / Dot では類似度降順、Euclidean では距離昇順 (内部で
    /// 符号反転して "高いほど近い" スコアに揃える) で結果を返す。
    /// ベクトルストアを持たないバックエンドは <see cref="NotSupportedException"/> を投げる。
    /// </remarks>
    VectorSearchCursor KnnSearch(string indexName, ReadOnlySpan<float> query, int k)
        => throw new NotSupportedException(
            "このバックエンドは KnnSearch を実装していません。access methods に IVectorStore を接続してください。");

    /// <summary>
    /// VEC-8: 同一インデックスに対する複数クエリを 1 回の呼び出しで投げる access path。
    /// 既定実装は <see cref="KnnSearch"/> を Q 回呼ぶフォールバック。in-memory backend は
    /// 単一 snapshot で Q×N をスコアリングするオーバーライドを提供する。
    /// </summary>
    IReadOnlyList<VectorSearchCursor> KnnSearchBatch(
        string indexName,
        IReadOnlyList<ReadOnlyMemory<float>> queries,
        int k)
    {
        ArgumentNullException.ThrowIfNull(queries);
        var arr = new VectorSearchCursor[queries.Count];
        for (int i = 0; i < queries.Count; i++)
            arr[i] = KnnSearch(indexName, queries[i].Span, k);
        return arr;
    }

    /// <summary>
    /// VEC-6: フィルタ付き KNN。<paramref name="candidates"/> のメンバーに限定して
    /// 上位 <paramref name="k"/> 件のベクトルを返す。既定実装は <see cref="KnnSearch"/> を
    /// オーバーサンプリング (k → 2k → 4k …) してポストフィルタを掛け、k 件揃うか
    /// オーバーサンプル上限に達するまで繰り返す。ANN 構造がサポートしていれば、
    /// バックエンドはベクトルインデックス側にフィルタを push down してよい。
    /// </summary>
    /// <remarks>
    /// codex_advice_3.md 6.4 節。スコア順序は維持され、類似度降順で返る。
    /// <paramref name="candidates"/> の <see cref="EntityCandidateSet.Kind"/> がインデックスの
    /// <see cref="EntityKind"/> と異なる場合は常に一致無しになる — これは構成誤りで、
    /// オペレータ層が顕在化させる責務であり、本契約違反ではない。
    /// </remarks>
    VectorSearchCursor KnnSearchFiltered(
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        EntityCandidateSet candidates)
        => KnnSearchFilteredOversample(this, indexName, query, k, candidates);

    /// <summary>
    /// VEC-6 既定実装の共有ヘルパ。VEC-8 でクラス側 override が高速経路を選んだあと、
    /// fallback 経路 (非 InMemory backend) でも同じオーバーサンプル挙動を呼べるよう抽出した。
    /// </summary>
    internal static VectorSearchCursor KnnSearchFilteredOversample(
        IGraphAccessMethods access,
        string indexName,
        ReadOnlySpan<float> query,
        int k,
        EntityCandidateSet candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k), k, "k は正の整数である必要があります。");

        if (candidates.Count == 0)
            return EmptyVectorSearchCursor.Instance;

        int oversampleCap = Math.Max(k * 64, candidates.Count * 2);
        int candidateK = Math.Min(Math.Max(k * 4, k + candidates.Count / 4), oversampleCap);

        while (true)
        {
            var hits = new List<VectorSearchResult>(k);
            using (var cursor = access.KnnSearch(indexName, query, candidateK))
            {
                while (cursor.MoveNext())
                {
                    var hit = cursor.Current;
                    if (!candidates.Contains(hit.EntityKind, hit.EntityId)) continue;
                    hits.Add(hit);
                    if (hits.Count >= k) break;
                }
            }

            if (hits.Count >= k || candidateK >= oversampleCap)
                return new MaterializedVectorSearchCursor(hits);

            candidateK = Math.Min(candidateK * 2, oversampleCap);
        }
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
public abstract class ExpandCursor : IDisposable
{
    /// <summary>次のエッジに進む。エッジを使い切ったら false を返す。</summary>
    public abstract bool MoveNext();

    /// <summary>現在エッジの隣接ノード ID。</summary>
    public abstract NodeId Neighbor { get; }

    /// <summary>現在エッジのリレーションシップ ID。</summary>
    public abstract RelationshipId Relationship { get; }

    /// <summary>
    /// BA-6: 現在エッジの生 64 ビット payload (典型的にはエッジ重み)。V2 隣接ビュー裏付けの
    /// カーソルは inline payload lane を転送する。それ以外のカーソルは 0 を返す。
    /// 有効な payload 種別が Double のときは <see cref="BitConverter.Int64BitsToDouble"/> で
    /// <see cref="double"/> として再解釈する。
    /// </summary>
    public virtual long WeightRaw => 0;

    /// <inheritdoc/>
    public virtual void Dispose() { }
}
