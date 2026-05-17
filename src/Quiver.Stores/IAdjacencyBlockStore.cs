using Quiver.Core;

namespace Quiver.Stores;

/// <summary>
/// 連続配置の隣接ブロックストアの読み取りインタフェース。
/// <c>BulkLoader.Commit(buildAdjacencyIndex: true)</c> で構築され、以降は不変として扱う。
/// </summary>
public interface IAdjacencyBlockStore
{
    /// <summary>指定ノードが隣接ブロックを持つ (= インデックス構築時に存在した) 場合に true を返す。</summary>
    bool HasBlock(NodeId nodeId);

    /// <summary>
    /// <paramref name="direction"/> と任意の <paramref name="typeFilter"/> にマッチするエッジを
    /// <paramref name="buffer"/> に詰めて、書き込んだ件数を返す。
    /// 戻り値が <c>buffer.Length</c> と等しい場合、ノードの degree がバッファサイズを超えている可能性があるため、
    /// 呼び出し側は正しさのためリンクリスト列挙 (または <see cref="OpenCursor"/>) にフォールバックすること。
    /// </summary>
    int ReadEdges(NodeId nodeId, Direction direction, RelationshipTypeId? typeFilter, AdjacencyEntry[] buffer);

    /// <summary>
    /// <paramref name="nodeId"/> の隣接ブロックチェーンに対するページ連続カーソルを開く。
    /// <see cref="ReadEdges"/> と異なり、このカーソルは決して打ち切らない: チェーン上の全ページを
    /// 辿り、エントリを 1 件ずつ放出するため、高 degree ノードでも固定サイズバッファが満杯になる
    /// 理由だけでリンクリスト経路にフォールバックする必要が無くなる。
    /// 隣接ブロックを持たないノードに対しては空カーソルを返す。
    /// </summary>
    AdjacencyCursor OpenCursor(NodeId nodeId, Direction direction, RelationshipTypeId? typeFilter);

    /// <summary>
    /// PW-14 / codex_advice_3 7.6 節。ベース隣接ビューの世代カウンタ (単調増加)。
    /// compact 時にインクリメントされる。永続化されたベースを持たないストアは 0 を返す。
    /// </summary>
    long Epoch => 0;

    /// <summary>
    /// PW-14: ベース構築時点のリレーション ID 高水位。
    /// id &lt; <see cref="BaseRelHwm"/> のリレーションシップは不変ベースビューに含まれ、
    /// id &gt;= はバルクロード後の delta レコードでリレーションシップリンクリストに置かれる。
    /// 0 はベースが存在しないことを示す。
    /// </summary>
    long BaseRelHwm => 0;

    /// <summary>
    /// PW-14: <paramref name="relId"/> がベースビューに属しつつビュー構築後に削除済みの場合に true を返す。
    /// expand カーソルはこのエントリをスキップするため、ベースを再構築せずに削除が反映される。
    /// </summary>
    bool IsTombstoned(RelationshipId relId) => false;

    /// <summary>
    /// PW-14: ベースのリレーションシップを削除済みとしてマークする。
    /// <c>relId.Value &gt;= <see cref="BaseRelHwm"/></c> のときは no-op となる — delta の削除は
    /// <c>RelationshipStore.Delete</c> が既に行うリンクリストのアンリンクのみで足りる。
    /// </summary>
    void Tombstone(RelationshipId relId) { }
}

/// <summary>
/// ノードの隣接ブロックチェーンに対するストリーミングカーソル。実装は同時に 1 ページだけを pin し、
/// 隣接ノード全体をマテリアライズせずにリンクされたページを辿る。
/// </summary>
public abstract class AdjacencyCursor : IDisposable
{
    /// <summary>次のマッチエントリへ進む。チェーンを使い切ったら false を返す。</summary>
    public abstract bool MoveNext();

    /// <summary>現在エントリの隣接ノード ID。<see cref="MoveNext"/> が true を返した後だけ有効。</summary>
    public abstract NodeId Neighbor { get; }

    /// <summary>現在エントリのリレーションシップ ID。<see cref="MoveNext"/> が true を返した後だけ有効。</summary>
    public abstract RelationshipId Relationship { get; }

    /// <summary>現在エントリのリレーションシップ型 ID。<see cref="MoveNext"/> が true を返した後だけ有効。</summary>
    public abstract RelationshipTypeId Type { get; }

    /// <summary>
    /// BA-6: payload lane を持つ <see cref="AdjacencyBlockStoreV2"/> 上でカーソルが開かれているときの、
    /// 現在エントリの生 64 ビット payload。V1 カーソルや payload lane 無しで構築された V2 カーソルでは 0 を返す。
    /// ストアの <see cref="PayloadKind"/> が <see cref="PayloadKind.Double"/> の場合は、
    /// <see cref="BitConverter.Int64BitsToDouble"/> で <c>double</c> として再解釈する。
    /// </summary>
    public virtual long WeightRaw => 0;

    /// <inheritdoc/>
    public virtual void Dispose() { }

    /// <summary>隣接ブロックを持たないノード用の共有空カーソル。</summary>
    public static AdjacencyCursor Empty { get; } = new EmptyAdjacencyCursor();

    private sealed class EmptyAdjacencyCursor : AdjacencyCursor
    {
        public override bool MoveNext() => false;
        public override NodeId Neighbor => NodeId.Invalid;
        public override RelationshipId Relationship => RelationshipId.Invalid;
        public override RelationshipTypeId Type => default;
    }
}

/// <summary>
/// BA-6: インライン payload lane (エッジ重み等のスカラ) を持つ隣接ストアのオプション拡張コントラクト。
/// オペレータは <c>tx.AdjacencyBlocks as IAdjacencyPayloadView</c> で能力検査し、
/// プロパティチェーンを経由せず <see cref="Quiver.Operators.ExpandOutputMode.NeighborAndWeight"/> 射影を選べる。
/// </summary>
public interface IAdjacencyPayloadView
{
    /// <summary>ビュー構築時に固定された payload 仕様。値で返す。</summary>
    PayloadLaneSpec PayloadSpec { get; }
}
