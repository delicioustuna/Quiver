namespace Quiver.Maintenance;

/// <summary>
/// 削除済みエンティティの物理回収・free list 圧縮・ファイル truncate を担う。
/// </summary>
/// <remarks>
/// MVP 実装はVertexストアのみを対象とする。Edge / プロパティ / B+Tree 索引の
/// 物理回収はチェーン整合性 (双方向リンク / blob 解放 / 索引 merge) の維持が必要なため
/// 後続のステップで拡張予定。前提として MVCC の <c>xmax</c> スタンプと
/// visibility horizon が必要。
/// </remarks>
public interface IVacuum
{
    /// <summary>
    /// 同期的に vacuum を実行する。アクティブトランザクションが残っているときは
    /// 何もせず <see cref="VacuumReport.Skipped"/> = true を返す
    /// (snapshot reader が dead version を辿る可能性があるため安全側)。
    /// </summary>
    VacuumReport Run(VacuumOptions? options = null);
}
