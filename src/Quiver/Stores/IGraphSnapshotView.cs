using Quiver.Core;

namespace Quiver.Storage.Records;

/// <summary>
/// アルゴリズム向けの読み取り専用グラフスナップショット。
/// 現在の隣接状態を CSR (out 用 compressed sparse row) と CSC (in 用 compressed sparse column)
/// 配列にマテリアライズすることで、PageRank・Louvain・繰り返し BFS / 最短経路など多パス系
/// アルゴリズムが、ページチェーン隣接ブロックに対する呼び出し毎カーソルではなく
/// フラットな <see cref="ReadOnlySpan{T}"/> で隣接を走査できるようにする。
///
/// ビューはポイントインタイム — スナップショットトランザクションはライターをブロックせずに
/// 安全に読み出せるが、構築後のミューテーションは可視化されない。<see cref="IDisposable.Dispose"/> を
/// 呼ぶと裏付け配列が <see cref="System.Buffers.ArrayPool{T}"/> に返却される。
///
/// <see cref="Epoch"/> は構築時点の <see cref="IAdjacencyBlockStore.Epoch"/> をそのまま反映する。
/// 呼び出し側はこの値で compact によるキャッシュ済み計算結果の無効化を検出できる。
/// </summary>
public interface IGraphSnapshotView : IDisposable
{
    /// <summary>スナップショット構築時にキャプチャした隣接エポック。ベースビューが無いときは 0。</summary>
    long Epoch { get; }

    /// <summary>
    /// このビューがインデックスする vertex スロット数。<c>[0, VertexCount)</c> 範囲のVertex ID が
    /// アドレス可能。範囲外の ID は空スパン / 0 degree を返す。
    /// </summary>
    long VertexCount { get; }

    /// <summary>ビューにマテリアライズした生存中Edgeの総数。</summary>
    long EdgeCount { get; }

    /// <summary>インライン重みレーンを持つ場合に true。false の場合 <see cref="WeightBitsOut"/> は空を返す。</summary>
    bool HasWeights { get; }

    /// <summary><paramref name="vertexId"/> の out-degree。範囲外なら 0。</summary>
    int OutDegree(VertexId vertexId);

    /// <summary><paramref name="vertexId"/> の in-degree。範囲外なら 0。</summary>
    int InDegree(VertexId vertexId);

    /// <summary><paramref name="vertexId"/> の outgoing 隣接Vertex ID 列 (各 out-edge の target)。</summary>
    ReadOnlySpan<long> OutNeighbors(VertexId vertexId);

    /// <summary><paramref name="vertexId"/> の incoming 隣接Vertex ID 列 (各 in-edge の source)。</summary>
    ReadOnlySpan<long> InNeighbors(VertexId vertexId);

    /// <summary><see cref="OutNeighbors"/> と並列のEdge ID 列。</summary>
    ReadOnlySpan<long> OutEdgeIds(VertexId vertexId);

    /// <summary><see cref="InNeighbors"/> と並列のEdge ID 列。</summary>
    ReadOnlySpan<long> InEdgeIds(VertexId vertexId);

    /// <summary>
    /// <see cref="OutNeighbors"/> と並列の生 64 ビット重み payload。ソースストアの payload 種別が
    /// <see cref="PayloadKind.Double"/> の場合は <see cref="BitConverter.Int64BitsToDouble"/> で再解釈する。
    /// <see cref="HasWeights"/> が false の場合は空。
    /// </summary>
    ReadOnlySpan<long> WeightBitsOut(VertexId vertexId);
}
