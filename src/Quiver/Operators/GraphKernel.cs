using Quiver.Core;
using Quiver.Storage.Records;
using Quiver.Transactions;

namespace Quiver.Query.Physical;

/// <summary>
/// PW-13 / codex_advice_3.md 7.8 節: BFS 系アルゴリズム (<see cref="BfsOperator"/>、
/// <see cref="VariableLengthExpandOperator"/>、<see cref="ShortestPathOperator"/>、
/// <see cref="ParallelBfsOperator"/>) が、近傍訪問ロジックを共有しつつ各オペレータが
/// 独自の状態形状 (frontier キュー、visited セット、距離マップなど) を保持できるようにする
/// コールバックコントラクト。
/// </summary>
/// <remarks>
/// カーネルは「近傍ごとに何をするか」だけを記述する。frontier スケジューリングと行マテリアライズは
/// オペレータ側に残し、コルーチン無しでも段階的 (Volcano) 反復が機能するようにしている。
/// 物理アクセス経路の選択 (隣接ブロック / リンクリスト / リレーションシップスキャン) は
/// <see cref="IGraphAccessMethods.Expand"/> に隠蔽されている。
/// <see cref="OneHopExpansion"/> はカーネルラッパが 1 ホップ走査時に呼ぶヘルパ。
/// </remarks>
public interface IGraphKernel<TState>
{
    /// <summary>
    /// 各ソースに対し、いずれのホップ展開も始まる前に 1 回だけ呼ばれる。
    /// 実装は <paramref name="state"/> 内の frontier / visited セットをシードする。
    /// </summary>
    void Initialize(NodeId source, ref TState state);

    /// <summary>
    /// <see cref="OneHopExpansion.Expand"/> が放出する近傍エッジ毎に呼ばれる。
    /// カーソル走査を継続するなら <c>true</c>、現ホップの残りを打ち切るなら <c>false</c> を返す
    /// (例: 最短経路の終点に到達)。
    /// </summary>
    /// <param name="weightRaw">
    /// <see cref="ExpandCursor.WeightRaw"/> から得る 64 ビット生 payload。payload lane を持たない
    /// カーソルは 0 を転送する。エッジ重みを必要としないカーネルは無視してよい (コストは
    /// <c>WeightRaw</c> への 1 度の仮想呼び出しのみ。payload 無しの一般的なバックエンドでは
    /// JIT が既にインライン化する)。
    /// </param>
    bool VisitNeighbor(
        NodeId source,
        NodeId target,
        RelationshipId relationshipId,
        long weightRaw,
        int depth,
        ref TState state);

    /// <summary>
    /// 各ホップ間で「次の frontier スロットをデキューするか」を判定するために呼ばれる。
    /// <paramref name="depth"/> はこれから展開するスロットの深さで、<c>0</c> はソース自身を意味する。
    /// </summary>
    bool ShouldContinue(int depth, in TState state);
}

/// <summary>
/// PW-13 / codex_advice_3.md §7.8: Single-hop expansion primitive shared by
/// BFS-style operators. Wraps <see cref="IGraphAccessMethods.Expand"/> so the
/// kernel never touches the underlying cursor; that lets the same algorithm
/// shell run over the binary backend's adjacency-block / linked-list path,
/// the SQLite backend's index path, or a future CSR snapshot view without
/// changes to the kernel.
/// </summary>
public static class OneHopExpansion
{
    /// <summary>
    /// Walk all edges incident to <paramref name="source"/> in
    /// <paramref name="direction"/> (optionally filtered by
    /// <paramref name="typeFilter"/>) and call
    /// <see cref="IGraphKernel{TState}.VisitNeighbor"/> for each neighbour.
    /// Returns <c>false</c> when the kernel asked to stop early; callers may
    /// use this signal to short-circuit the outer frontier loop (shortest path).
    /// </summary>
    public static bool Expand<TState>(
        ITransaction tx,
        NodeId source,
        Direction direction,
        RelationshipTypeId? typeFilter,
        int depth,
        IGraphKernel<TState> kernel,
        ref TState state)
    {
        using var cursor = tx.Access.Expand(tx, source, direction, typeFilter);
        while (cursor.MoveNext())
        {
            if (!kernel.VisitNeighbor(
                    source,
                    cursor.Neighbor,
                    cursor.Relationship,
                    cursor.WeightRaw,
                    depth,
                    ref state))
                return false;
        }
        return true;
    }
}
