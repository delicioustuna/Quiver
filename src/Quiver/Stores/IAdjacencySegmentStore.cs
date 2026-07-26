using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// 連続配置の隣接ブロックストアの読み取りインタフェース。
/// <c>BulkLoader.Commit(buildAdjacencyIndex: true)</c> で構築され、以降は不変として扱う。
/// </summary>
internal interface IAdjacencySegmentStore
{
    /// <summary>指定Vertexが隣接ブロックを持つ (= インデックス構築時に存在した) 場合に true を返す。</summary>
    bool HasBlock(VertexId vertexId);

    /// <summary>
    /// <paramref name="direction"/> と任意の <paramref name="typeFilter"/> にマッチするエッジを
    /// <paramref name="buffer"/> に詰めて、書き込んだ件数を返す。
    /// 戻り値が <c>buffer.Length</c> と等しい場合、Vertexの degree がバッファサイズを超えている可能性があるため、
    /// 呼び出し側は正しさのためリンクリスト列挙 (または <see cref="OpenCursor"/>) にフォールバックすること。
    /// </summary>
    int ReadEdges(VertexId vertexId, Direction direction, EdgeTypeId? typeFilter, AdjacencyEntry[] buffer);

    /// <summary>
    /// <paramref name="vertexId"/> の隣接ブロックチェーンに対するページ連続カーソルを開く。
    /// <see cref="ReadEdges"/> と異なり、このカーソルは決して打ち切らない: チェーン上の全ページを
    /// 辿り、エントリを 1 件ずつ放出するため、高 degree Vertexでも固定サイズバッファが満杯になる
    /// 理由だけでリンクリスト経路にフォールバックする必要が無くなる。
    /// 隣接ブロックを持たないVertexに対しては空カーソルを返す。
    /// </summary>
    AdjacencyCursor OpenCursor(VertexId vertexId, Direction direction, EdgeTypeId? typeFilter);

    /// <summary>
    /// ベース隣接ビューの世代カウンタ (単調増加)。
    /// compact 時にインクリメントされる。永続化されたベースを持たないストアは 0 を返す。
    /// </summary>
    long Epoch => 0;

    /// <summary>
    /// ベース構築時点のリレーション ID 高水位。
    /// id &lt; <see cref="BaseEdgeHwm"/> のEdgeは不変ベースビューに含まれ、
    /// id &gt;= はバルクロード後の delta レコードでEdgeリンクリストに置かれる。
    /// 0 はベースが存在しないことを示す。
    /// </summary>
    long BaseEdgeHwm => 0;

    /// <summary>
    /// <paramref name="edgeId"/> がベースビューに属しつつビュー構築後に削除済みの場合に true を返す。
    /// expand カーソルはこのエントリをスキップするため、ベースを再構築せずに削除が反映される。
    /// </summary>
    bool IsTombstoned(EdgeId edgeId) => false;

    /// <summary>
    /// ベースのEdgeを削除済みとしてマークする。
    /// <c>edgeId.Value &gt;= <see cref="BaseEdgeHwm"/></c> のときは no-op となる — delta の削除は
    /// <c>EdgeStore.Delete</c> が既に行うリンクリストのアンリンクのみで足りる。
    /// </summary>
    void Tombstone(EdgeId edgeId) { }
}

/// <summary>
/// Vertexの隣接ブロックチェーンに対するストリーミングカーソル。実装は同時に 1 ページだけを pin し、
/// 隣接Vertex全体をマテリアライズせずにリンクされたページを辿る。
/// </summary>
internal abstract class AdjacencyCursor : IDisposable
{
    /// <summary>次のマッチエントリへ進む。チェーンを使い切ったら false を返す。</summary>
    public abstract bool MoveNext();

    /// <summary>現在エントリの隣接Vertex ID。<see cref="MoveNext"/> が true を返した後だけ有効。</summary>
    public abstract VertexId Neighbor { get; }

    /// <summary>現在エントリのEdge ID。<see cref="MoveNext"/> が true を返した後だけ有効。</summary>
    public abstract EdgeId Edge { get; }

    /// <summary>現在エントリのEdge型 ID。<see cref="MoveNext"/> が true を返した後だけ有効。</summary>
    public abstract EdgeTypeId Type { get; }

    /// <summary>
    /// payload lane を持つ <see cref="AdjacencySegmentStore"/> 上でカーソルが開かれているときの、
    /// 現在エントリの生 64 ビット payload。payload lane 無しの場合は 0 を返す。
    /// ストアの <see cref="PayloadKind"/> が <see cref="PayloadKind.Double"/> の場合は、
    /// <see cref="BitConverter.Int64BitsToDouble"/> で <c>double</c> として再解釈する。
    /// </summary>
    public virtual long WeightRaw => 0;

    /// <inheritdoc/>
    public virtual void Dispose() { }

    /// <summary>隣接ブロックを持たないVertex用の共有空カーソル。</summary>
    public static AdjacencyCursor Empty { get; } = new EmptyAdjacencyCursor();

    private sealed class EmptyAdjacencyCursor : AdjacencyCursor
    {
        public override bool MoveNext() => false;
        public override VertexId Neighbor => VertexId.Invalid;
        public override EdgeId Edge => EdgeId.Invalid;
        public override EdgeTypeId Type => default;
    }
}

/// <summary>
/// インライン payload lane (エッジ重み等のスカラ) を持つ隣接ストアのオプション拡張コントラクト。
/// オペレータは <c>tx.AdjacencySegments as IAdjacencyPayloadView</c> で能力検査し、
/// プロパティチェーンを経由せず <see cref="Quiver.Operators.ExpandOutputMode.NeighborAndWeight"/> 射影を選べる。
/// </summary>
internal interface IAdjacencyPayloadView
{
    /// <summary>ビュー構築時に固定された payload 仕様。値で返す。</summary>
    PayloadLaneSpec PayloadSpec { get; }
}
