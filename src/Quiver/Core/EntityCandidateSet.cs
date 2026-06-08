namespace Quiver.Core;

/// <summary>
/// <c>IGraphAccessMethods.KnnSearchFiltered</c> の所属判定側として使う、
/// 事前計算済みのエンティティ ID 集合。graph-first プランが (label / property / 近傍マッチによる)
/// 候補 frontier を vector access path に渡すことで、ベクトルインデックスを関連ノードだけに
/// スコアリング対象を絞ることができる。
/// </summary>
/// <remarks>
/// 現状は <see cref="HashSet{T}"/> 裏付け。<c>FrontierSet</c> 系のビットマップバリアントは、
/// dense なノード ID パターンで効果が出るようになった段階で追加するのが妥当 — フィルタ付き KNN の
/// 初期の正しさ経路 (codex_advice_3.md 6.4 節) では不要。
/// </remarks>
public sealed class EntityCandidateSet
{
    private readonly HashSet<long> _ids;

    /// <summary>種別 + 候補 ID 集合で <see cref="EntityCandidateSet"/> を生成する。</summary>
    public EntityCandidateSet(EntityKind kind, IEnumerable<long> ids)
    {
        Kind = kind;
        _ids = new HashSet<long>(ids);
    }

    /// <summary>候補集合の対象エンティティ種別。</summary>
    public EntityKind Kind { get; }

    /// <summary>候補件数。</summary>
    public int Count => _ids.Count;

    /// <summary>
    /// 候補 ID の列挙。in-memory backend が gather パス
    /// (candidate ID を直接ルックアップ) を取れるよう露出する。順序は保証しない。
    /// </summary>
    public IEnumerable<long> Ids => _ids;

    /// <summary>ID を所属判定する。種別は問わない。</summary>
    public bool Contains(long id) => _ids.Contains(id);

    /// <summary>種別 + ID で所属判定する。種別が一致しなければ常に false。</summary>
    public bool Contains(EntityKind kind, long id) => kind == Kind && _ids.Contains(id);
}
