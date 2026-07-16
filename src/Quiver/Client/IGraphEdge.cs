using Quiver.Core;

namespace Quiver.Api;

/// <summary>
/// SourceGenerator が <c>[Edge]</c> 付与クラスに自動実装する
/// 型安全Edge CRUD のためのインタフェース。手動実装は通常不要。
/// </summary>
/// <typeparam name="TSelf">自分自身の型 (CRTP)。</typeparam>
public interface IGraphEdge<TSelf> where TSelf : IGraphEdge<TSelf>
{
    /// <summary>Edge型名。SourceGenerator が <c>[Edge("...")]</c> から決定する。</summary>
    static abstract string GraphType { get; }

    /// <summary>新規Edgeを作成してプロパティを書き込み、その ID を返す。</summary>
    static abstract EdgeId Insert(IGraphTransaction tx, VertexId from, VertexId to, TSelf entity);

    /// <summary>指定 ID のEdgeを読み込んでインスタンスを復元する。</summary>
    static abstract TSelf Load(IGraphTransaction tx, EdgeId id);

    /// <summary>指定 ID のEdgeのプロパティを上書きする。</summary>
    static abstract void Update(IGraphTransaction tx, EdgeId id, TSelf entity);

    /// <summary>指定 ID のEdgeを削除する。</summary>
    static abstract void Delete(IGraphTransaction tx, EdgeId id);
}

/// <summary>
/// 始点 (<typeparamref name="TSource"/>) / 終点 (<typeparamref name="TTarget"/>) Vertex型を
/// 型レベルで保持するEdge。SourceGenerator が
/// <c>[Edge&lt;TSource, TTarget&gt;]</c> から自動実装する。CRUD 契約は
/// 基底 <see cref="IGraphEdge{TSelf}"/> から継承し、本インタフェースは端点型の
/// 制約を足すだけ (追加メンバーなし)。これにより <c>TypedGraphTraversal&lt;TSource&gt;</c> の
/// <c>Out&lt;TEdge, TTarget&gt;</c> がホップ間で型を保存できる。
/// </summary>
/// <typeparam name="TSelf">自分自身の型 (CRTP)。</typeparam>
/// <typeparam name="TSource">始点Vertex型。</typeparam>
/// <typeparam name="TTarget">終点Vertex型。</typeparam>
public interface IGraphEdge<TSelf, TSource, TTarget> : IGraphEdge<TSelf>
    where TSelf   : IGraphEdge<TSelf, TSource, TTarget>
    where TSource : IGraphVertex<TSource>
    where TTarget : IGraphVertex<TTarget>
{
}
