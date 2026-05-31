using Quiver.Core;

namespace Quiver.Client;

/// <summary>
/// SourceGenerator が <c>[GraphRelationship]</c> 付与クラスに自動実装する
/// 型安全リレーションシップ CRUD のためのインタフェース。手動実装は通常不要。
/// </summary>
/// <typeparam name="TSelf">自分自身の型 (CRTP)。</typeparam>
public interface IGraphRelationship<TSelf> where TSelf : IGraphRelationship<TSelf>
{
    /// <summary>リレーションシップ型名。SourceGenerator が <c>[GraphRelationship("...")]</c> から決定する。</summary>
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
