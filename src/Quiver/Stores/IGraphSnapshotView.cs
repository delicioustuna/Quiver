using Quiver.Core;

namespace Quiver.Stores;

/// <summary>
/// PW-15 / codex_advice_3 7.7 節。アルゴリズム向けの読み取り専用グラフスナップショット。
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
    /// このビューがインデックスする node スロット数。<c>[0, NodeCount)</c> 範囲のノード ID が
    /// アドレス可能。範囲外の ID は空スパン / 0 degree を返す。
    /// </summary>
    long NodeCount { get; }

    /// <summary>ビューにマテリアライズした生存中リレーションシップの総数。</summary>
    long EdgeCount { get; }

    /// <summary>インライン重みレーンを持つ場合に true。false の場合 <see cref="WeightBitsOut"/> は空を返す。</summary>
    bool HasWeights { get; }

    /// <summary><paramref name="nodeId"/> の out-degree。範囲外なら 0。</summary>
    int OutDegree(NodeId nodeId);

    /// <summary><paramref name="nodeId"/> の in-degree。範囲外なら 0。</summary>
    int InDegree(NodeId nodeId);

    /// <summary><paramref name="nodeId"/> の outgoing 隣接ノード ID 列 (各 out-edge の target)。</summary>
    ReadOnlySpan<long> OutNeighbors(NodeId nodeId);

    /// <summary><paramref name="nodeId"/> の incoming 隣接ノード ID 列 (各 in-edge の source)。</summary>
    ReadOnlySpan<long> InNeighbors(NodeId nodeId);

    /// <summary><see cref="OutNeighbors"/> と並列のリレーションシップ ID 列。</summary>
    ReadOnlySpan<long> OutRelationshipIds(NodeId nodeId);

    /// <summary><see cref="InNeighbors"/> と並列のリレーションシップ ID 列。</summary>
    ReadOnlySpan<long> InRelationshipIds(NodeId nodeId);

    /// <summary>
    /// <see cref="OutNeighbors"/> と並列の生 64 ビット重み payload。ソースストアの payload 種別が
    /// <see cref="PayloadKind.Double"/> の場合は <see cref="BitConverter.Int64BitsToDouble"/> で再解釈する。
    /// <see cref="HasWeights"/> が false の場合は空。
    /// </summary>
    ReadOnlySpan<long> WeightBitsOut(NodeId nodeId);
}
