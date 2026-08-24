namespace Yatagarasu.Maintenance;

/// <summary>
/// visibility horizon を越えた dead version、導出 manifest、未参照 artifact の物理回収を担う。
/// </summary>
/// <remarks>
/// active reader が存在しても、その reader が固定した最古の visibility horizon より前だけを
/// 回収する。
/// writer lease は maintenance mutation の直列化に使うが、reader の終了は待たない。
/// </remarks>
public interface IVacuum
{
    /// <summary>
    /// 同期的に vacuum を実行し、active reader が参照し得ない version だけを回収する。
    /// 未対応バックエンドは <see cref="VacuumReport.Skipped"/> = <see langword="true"/> を返す。
    /// </summary>
    VacuumReport Run(VacuumOptions? options = null);
}
