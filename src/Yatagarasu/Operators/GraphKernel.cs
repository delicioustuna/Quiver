using Yatagarasu.Core;
using Yatagarasu.Storage.Records;
using Yatagarasu.Transactions;

namespace Yatagarasu.Query.Physical;

/// <summary>
/// BFS 系アルゴリズム (<see cref="BfsOperator"/>、
/// <see cref="VariableLengthExpandOperator"/>、<see cref="ShortestPathOperator"/>、
/// <see cref="ParallelBfsOperator"/>) が、近傍訪問ロジックを共有しつつ各オペレータが
/// 独自の状態形状 (frontier キュー、visited セット、距離マップなど) を保持できるようにする
/// コールバックコントラクト。
/// </summary>
/// <remarks>
/// カーネルは「近傍ごとに何をするか」だけを記述する。frontier スケジューリングと行マテリアライズは
/// オペレータ側に残し、コルーチン無しでも段階的 (Volcano) 反復が機能するようにしている。
/// 物理アクセス経路の選択 (隣接ブロック / リンクリスト / Edgeスキャン) は
/// <see cref="IGraphAccessMethods.Expand"/> に隠蔽されている。
/// <see cref="OneHopExpansion"/> はカーネルラッパが 1 ホップ走査時に呼ぶヘルパ。
/// </remarks>
internal interface IGraphKernel<TState>
{
    /// <summary>
    /// 各ソースに対し、いずれのホップ展開も始まる前に 1 回だけ呼ばれる。
    /// 実装は <paramref name="state"/> 内の frontier / visited セットをシードする。
    /// </summary>
    void Initialize(VertexId source, ref TState state);

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
        VertexId source,
        VertexId target,
        EdgeId edgeId,
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
/// BFS 系オペレータが共有する 1 ホップ展開プリミティブ。
/// <see cref="IGraphAccessMethods.Expand"/> をラップしてカーネルが基盤カーソルに
/// 触れないようにし、同一アルゴリズムシェルが隣接ブロック / リンクリスト経路でも
/// 将来の CSR スナップショットビューでもカーネル変更なしで動作する。
/// </summary>
internal static class OneHopExpansion
{
    /// <summary>
    /// <paramref name="source"/> に接する全エッジを <paramref name="direction"/> 方向に走査し
    /// (オプションで <paramref name="typeFilter"/> 適用)、各近傍に対して
    /// <see cref="IGraphKernel{TState}.VisitNeighbor"/> を呼ぶ。
    /// カーネルが早期停止を要求した場合は <c>false</c> を返す
    /// (最短経路で外側 frontier ループを短絡するために使える)。
    /// </summary>
    public static bool Expand<TState>(
        ITransaction tx,
        VertexId source,
        Direction direction,
        EdgeTypeId? typeFilter,
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
                    cursor.Edge,
                    cursor.WeightRaw,
                    depth,
                    ref state))
                return false;
        }
        return true;
    }
}
