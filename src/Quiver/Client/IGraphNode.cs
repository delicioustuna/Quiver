using Quiver.Core;

namespace Quiver.Api;

/// <summary>
/// SourceGenerator が <c>[GraphNode]</c> 付与クラスに自動実装する型安全ノード CRUD のためのインタフェース。
/// 静的抽象メンバーで CRUD 操作のシグネチャを定義し、<c>TypedGraphTraversal&lt;T&gt;</c> のような
/// 型付き API から呼び出される。手動実装は通常不要。
/// </summary>
/// <typeparam name="TSelf">自分自身の型 (CRTP)。</typeparam>
public interface IGraphNode<TSelf> where TSelf : IGraphNode<TSelf>
{
    /// <summary>ノードに付与するラベル名。SourceGenerator が <c>[GraphNode("...")]</c> から決定する。</summary>
    static abstract string GraphLabel { get; }

    /// <summary>新規ノードを作成してプロパティを書き込み、その ID を返す。</summary>
    static abstract NodeId Insert(IGraphTransaction tx, TSelf entity);

    /// <summary><see cref="Insert"/> に加え、<c>[GraphIndexed]</c> プロパティを対応インデックスへ登録する。</summary>
    static abstract NodeId InsertIndexed(IGraphTransaction tx, TSelf entity);

    /// <summary>指定 ID のノードを読み込み、<typeparamref name="TSelf"/> インスタンスとして復元する。</summary>
    static abstract TSelf Load(IGraphTransaction tx, NodeId id);

    /// <summary>指定 ID のノードのプロパティを <paramref name="entity"/> で上書きする。</summary>
    static abstract void Update(IGraphTransaction tx, NodeId id, TSelf entity);

    /// <summary>指定 ID のノードを削除する。</summary>
    static abstract void Delete(IGraphTransaction tx, NodeId id);
}
