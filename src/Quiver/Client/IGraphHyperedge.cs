using Quiver.Core;

namespace Quiver.Api;

/// <summary>
/// SourceGenerator が <c>[Hyperedge]</c> 付与クラスに自動実装する型安全ハイパーエッジ
/// CRUD のためのインタフェース。静的抽象メンバーで CRUD 操作のシグネチャを定義する。
/// 手動実装は通常不要。
/// </summary>
/// <remarks>
/// ハイパーエッジのアリティ (メンバー数) はロールごとに型付けされ、可変アリティに依存しない
/// 最小契約のみをここで定義する。ロール束縛は <c>Insert</c> でエンティティのロールプロパティ
/// から組み立てて一度だけ書き込み、以後 <c>Update</c> では変更しない。
/// </remarks>
/// <typeparam name="TSelf">自分自身の型 (CRTP)。</typeparam>
public interface IGraphHyperedge<TSelf> where TSelf : IGraphHyperedge<TSelf>
{
    /// <summary>ハイパーエッジ型名。SourceGenerator が <c>[Hyperedge("...")]</c> から決定する。</summary>
    static abstract string GraphType { get; }

    /// <summary>ロール束縛とプロパティを書き込んで新規ハイパーエッジを作成し、その ID を返す。</summary>
    static abstract HyperedgeId Insert(IGraphTransaction tx, TSelf entity);

    /// <summary>指定 ID のハイパーエッジを読み込み、ロール束縛とプロパティを <typeparamref name="TSelf"/> へ復元する。</summary>
    static abstract TSelf Load(IGraphTransaction tx, HyperedgeId id);

    /// <summary>指定 ID のハイパーエッジのプロパティを <paramref name="entity"/> で上書きする (ロール束縛は不変)。</summary>
    static abstract void Update(IGraphTransaction tx, HyperedgeId id, TSelf entity);

    /// <summary>指定 ID のハイパーエッジを削除する。</summary>
    static abstract void Delete(IGraphTransaction tx, HyperedgeId id);
}
