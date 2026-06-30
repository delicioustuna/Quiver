using Quiver.Core;

namespace Quiver.Api;

/// <summary>
/// 型付きトラバーサルを終端として辺を一括生成する <c>AddRelationship</c> / <c>MergeRelationship</c> 拡張。
/// 端点の型整合は <c>IGraphRelationship&lt;TRel, TSource, TTarget&gt;</c> 制約で保証される。
/// </summary>
public static class TypedGraphTraversalWriteExtensions
{
    // 設計: 両端を先に materialize してからループ書き込みする (materialize-first)。
    // これにより書き込んだ辺が上流走査へ再投入される Halloween 問題が構造的に起きず、
    // 始点と終点が同一ラベルでも辺生成は有限回で止まる。
        // プロパティ無し版は new TRel() のデフォルト値を書かないよう CreateRelationship を直接使い、
    // ID のみで足りるので MaterializeIds でエンティティ復元を省く。
    // MergeRelationship のプロパティは ON CREATE のみ書く (既存辺は保持。MergeNode と対称)。

    // ── AddRelationship: 直積で常に辺を生成 ─────────────────────────────────────────────

    /// <summary>始点集合と終点集合の直積に辺を生成し、生成本数を返す。</summary>
    public static long AddRelationship<TSource, TRel, TTarget>(
        this TypedGraphTraversal<TSource> sources,
        TypedGraphTraversal<TTarget> targets,
        Func<TSource, TTarget, TRel> edge)
        where TSource : IGraphNode<TSource>
        where TTarget : IGraphNode<TTarget>
        where TRel : IGraphRelationship<TRel, TSource, TTarget>
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(edge);

        var tx = sources.Transaction;
        var src = sources.ToListWithIds();
        var dst = targets.ToListWithIds();   // 直積なので終点は一度だけ materialize し全始点で再利用。

        long created = 0;
        foreach (var (sid, sEntity) in src)
            foreach (var (tid, tEntity) in dst)
            {
                TRel.Insert(tx, sid, tid, edge(sEntity, tEntity));
                created++;
            }
        return created;
    }

    /// <summary>直積にプロパティ無しの辺を生成し、生成本数を返す。</summary>
    public static long AddRelationship<TSource, TRel, TTarget>(
        this TypedGraphTraversal<TSource> sources,
        TypedGraphTraversal<TTarget> targets)
        where TSource : IGraphNode<TSource>
        where TTarget : IGraphNode<TTarget>
        where TRel : IGraphRelationship<TRel, TSource, TTarget>
    {
        ArgumentNullException.ThrowIfNull(targets);

        var tx = sources.Transaction;
        var src = sources.MaterializeIds();
        var dst = targets.MaterializeIds();

        long created = 0;
        foreach (var sid in src)
            foreach (var tid in dst)
            {
                tx.CreateRelationship(sid, tid, TRel.GraphType);
                created++;
            }
        return created;
    }

    /// <summary>始点ごとに <paramref name="targets"/> で終点集合を求め、その直積に辺を生成する。</summary>
    public static long AddRelationship<TSource, TRel, TTarget>(
        this TypedGraphTraversal<TSource> sources,
        Func<TSource, TypedGraphTraversal<TTarget>> targets,
        Func<TSource, TTarget, TRel> edge)
        where TSource : IGraphNode<TSource>
        where TTarget : IGraphNode<TTarget>
        where TRel : IGraphRelationship<TRel, TSource, TTarget>
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(edge);

        var tx = sources.Transaction;
        var src = sources.ToListWithIds();   // 始点を先に確定してから終点を都度評価する。

        long created = 0;
        foreach (var (sid, sEntity) in src)
            foreach (var (tid, tEntity) in targets(sEntity).ToListWithIds())
            {
                TRel.Insert(tx, sid, tid, edge(sEntity, tEntity));
                created++;
            }
        return created;
    }

    /// <summary>相関版のプロパティ無し。</summary>
    public static long AddRelationship<TSource, TRel, TTarget>(
        this TypedGraphTraversal<TSource> sources,
        Func<TSource, TypedGraphTraversal<TTarget>> targets)
        where TSource : IGraphNode<TSource>
        where TTarget : IGraphNode<TTarget>
        where TRel : IGraphRelationship<TRel, TSource, TTarget>
    {
        ArgumentNullException.ThrowIfNull(targets);

        var tx = sources.Transaction;
        var src = sources.ToListWithIds();

        long created = 0;
        foreach (var (sid, sEntity) in src)
            foreach (var tid in targets(sEntity).MaterializeIds())
            {
                tx.CreateRelationship(sid, tid, TRel.GraphType);
                created++;
            }
        return created;
    }

    // ── MergeRelationship: 直積で upsert ────────────────────────────────────────────────

    /// <summary>直積を upsert する。無ければ生成してプロパティを書き、戻り値は (新規, 既存ヒット) の本数。</summary>
    public static (long Created, long Matched) MergeRelationship<TSource, TRel, TTarget>(
        this TypedGraphTraversal<TSource> sources,
        TypedGraphTraversal<TTarget> targets,
        Func<TSource, TTarget, TRel> edge)
        where TSource : IGraphNode<TSource>
        where TTarget : IGraphNode<TTarget>
        where TRel : IGraphRelationship<TRel, TSource, TTarget>
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(edge);

        var tx = sources.Transaction;
        var src = sources.ToListWithIds();
        var dst = targets.ToListWithIds();

        long created = 0, matched = 0;
        foreach (var (sid, sEntity) in src)
            foreach (var (tid, tEntity) in dst)
            {
                var (id, isNew) = tx.MergeRelationship(sid, tid, TRel.GraphType);
                if (isNew) { TRel.Update(tx, id, edge(sEntity, tEntity)); created++; }
                else matched++;
            }
        return (created, matched);
    }

    /// <summary>直積をプロパティ無しで upsert する。戻り値は (新規, 既存ヒット) の本数。</summary>
    public static (long Created, long Matched) MergeRelationship<TSource, TRel, TTarget>(
        this TypedGraphTraversal<TSource> sources,
        TypedGraphTraversal<TTarget> targets)
        where TSource : IGraphNode<TSource>
        where TTarget : IGraphNode<TTarget>
        where TRel : IGraphRelationship<TRel, TSource, TTarget>
    {
        ArgumentNullException.ThrowIfNull(targets);

        var tx = sources.Transaction;
        var src = sources.MaterializeIds();
        var dst = targets.MaterializeIds();

        long created = 0, matched = 0;
        foreach (var sid in src)
            foreach (var tid in dst)
            {
                var (_, isNew) = tx.MergeRelationship(sid, tid, TRel.GraphType);
                if (isNew) created++; else matched++;
            }
        return (created, matched);
    }

    /// <summary>始点ごとに終点集合を求め、その直積を upsert する。</summary>
    public static (long Created, long Matched) MergeRelationship<TSource, TRel, TTarget>(
        this TypedGraphTraversal<TSource> sources,
        Func<TSource, TypedGraphTraversal<TTarget>> targets,
        Func<TSource, TTarget, TRel> edge)
        where TSource : IGraphNode<TSource>
        where TTarget : IGraphNode<TTarget>
        where TRel : IGraphRelationship<TRel, TSource, TTarget>
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(edge);

        var tx = sources.Transaction;
        var src = sources.ToListWithIds();

        long created = 0, matched = 0;
        foreach (var (sid, sEntity) in src)
            foreach (var (tid, tEntity) in targets(sEntity).ToListWithIds())
            {
                var (id, isNew) = tx.MergeRelationship(sid, tid, TRel.GraphType);
                if (isNew) { TRel.Update(tx, id, edge(sEntity, tEntity)); created++; }
                else matched++;
            }
        return (created, matched);
    }

    /// <summary>相関版のプロパティ無し upsert。</summary>
    public static (long Created, long Matched) MergeRelationship<TSource, TRel, TTarget>(
        this TypedGraphTraversal<TSource> sources,
        Func<TSource, TypedGraphTraversal<TTarget>> targets)
        where TSource : IGraphNode<TSource>
        where TTarget : IGraphNode<TTarget>
        where TRel : IGraphRelationship<TRel, TSource, TTarget>
    {
        ArgumentNullException.ThrowIfNull(targets);

        var tx = sources.Transaction;
        var src = sources.ToListWithIds();

        long created = 0, matched = 0;
        foreach (var (sid, sEntity) in src)
            foreach (var tid in targets(sEntity).MaterializeIds())
            {
                var (_, isNew) = tx.MergeRelationship(sid, tid, TRel.GraphType);
                if (isNew) created++; else matched++;
            }
        return (created, matched);
    }
}
