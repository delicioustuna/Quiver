using Quiver.Core;

namespace Quiver.Api;

/// <summary>
/// SourceGenerator が <c>[Relationship]</c> 付与クラスに自動実装する
/// 型安全リレーションシップ CRUD のためのインタフェース。手動実装は通常不要。
/// </summary>
/// <typeparam name="TSelf">自分自身の型 (CRTP)。</typeparam>
public interface IGraphRelationship<TSelf> where TSelf : IGraphRelationship<TSelf>
{
    /// <summary>リレーションシップ型名。SourceGenerator が <c>[Relationship("...")]</c> から決定する。</summary>
    static abstract string GraphType { get; }

    /// <summary>新規リレーションシップを作成してプロパティを書き込み、その ID を返す。</summary>
    static abstract RelationshipId Insert(IGraphTransaction tx, NodeId from, NodeId to, TSelf entity);

    /// <summary>指定 ID のリレーションシップを読み込んでインスタンスを復元する。</summary>
    static abstract TSelf Load(IGraphTransaction tx, RelationshipId id);

    /// <summary>指定 ID のリレーションシップのプロパティを上書きする。</summary>
    static abstract void Update(IGraphTransaction tx, RelationshipId id, TSelf entity);

    /// <summary>指定 ID のリレーションシップを削除する。</summary>
    static abstract void Delete(IGraphTransaction tx, RelationshipId id);
}

/// <summary>
/// 始点 (<typeparamref name="TSource"/>) / 終点 (<typeparamref name="TTarget"/>) ノード型を
/// 型レベルで保持するリレーションシップ。SourceGenerator が
/// <c>[Relationship&lt;TSource, TTarget&gt;]</c> から自動実装する。CRUD 契約は
/// 基底 <see cref="IGraphRelationship{TSelf}"/> から継承し、本インタフェースは端点型の
/// 制約を足すだけ (追加メンバーなし)。これにより <c>TypedGraphTraversal&lt;TSource&gt;</c> の
/// <c>Out&lt;TRel, TTarget&gt;</c> がホップ間で型を保存できる。
/// </summary>
/// <typeparam name="TSelf">自分自身の型 (CRTP)。</typeparam>
/// <typeparam name="TSource">始点ノード型。</typeparam>
/// <typeparam name="TTarget">終点ノード型。</typeparam>
public interface IGraphRelationship<TSelf, TSource, TTarget> : IGraphRelationship<TSelf>
    where TSelf   : IGraphRelationship<TSelf, TSource, TTarget>
    where TSource : IGraphNode<TSource>
    where TTarget : IGraphNode<TTarget>
{
}
