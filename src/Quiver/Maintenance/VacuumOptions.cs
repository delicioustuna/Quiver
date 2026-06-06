namespace Quiver.Maintenance;

/// <summary>
/// OP-3 vacuum 実行ポリシー。
/// </summary>
public enum VacuumPolicy
{
    /// <summary>手動実行のみ (既定)。<see cref="GraphDatabaseOptions.AutoVacuum"/> が false の場合。</summary>
    Manual,
    /// <summary>低頻度のバックグラウンドワーカーで自動実行。未実装の MVP では Manual と同義。</summary>
    Auto,
}

/// <summary>
/// OP-3 vacuum 実行モード。
/// </summary>
public enum VacuumMode
{
    /// <summary>
    /// 全エンティティを走査して dead version を物理回収する。MVP ではノードのみ。
    /// </summary>
    Full,
    /// <summary>
    /// 検出のみ。ファイルには触らない。<see cref="VacuumReport"/> は「もし実行したら何件回収するか」のドライラン結果を返す。
    /// </summary>
    DryRun,
}

/// <summary>
/// OP-3 vacuum オプション。
/// </summary>
public sealed class VacuumOptions
{
    /// <summary>実行モード。既定 <see cref="VacuumMode.Full"/>。</summary>
    public VacuumMode Mode { get; init; } = VacuumMode.Full;

    /// <summary>
    /// 実行を打ち切る上限時間 (ミリ秒)。<c>0</c> 以下で制限なし。MVP では advisory にしか
    /// 使われず、ノード走査の各ステップ後にチェックされる。既定 0 (制限なし)。
    /// </summary>
    public int MaxDurationMs { get; init; }

    /// <summary>
    /// 対象エンティティ種別のビットマスク。MVP では <see cref="VacuumTarget.Nodes"/> のみ。
    /// </summary>
    public VacuumTarget Targets { get; init; } = VacuumTarget.All;
}

/// <summary>OP-3: vacuum 対象エンティティの指定。複数ビット同時指定可。</summary>
[System.Flags]
public enum VacuumTarget
{
    None = 0,
    Nodes = 1 << 0,
    Relationships = 1 << 1,
    Properties = 1 << 2,
    Indexes = 1 << 3,
    All = Nodes | Relationships | Properties | Indexes,
}

/// <summary>
/// OP-3 vacuum 実行結果。
/// </summary>
/// <param name="ReclaimedNodes">物理回収した dead ノード版数。</param>
/// <param name="ReclaimedRelationships">物理回収した dead リレーションシップ版数 (MVP は 0)。</param>
/// <param name="ReclaimedProperties">物理回収した dead プロパティ版数 (MVP は 0)。</param>
/// <param name="PrunedCommittedTxEntries">visibility horizon を下回り削除した committed registry エントリ数。</param>
/// <param name="ElapsedMs">実行に要した時間 (ミリ秒)。</param>
/// <param name="HorizonTxId">この実行で採用した visibility horizon。これ未満の xmax を持つ dead version が回収対象。</param>
/// <param name="Skipped">前提 (アクティブ tx 0) を満たせず未実行のとき true。</param>
/// <param name="TruncatedPages">OP-5: 物理 truncate で nodes/rels/props 3 ストア合計から削減したページ数。</param>
/// <param name="ReclaimedColumnVersions">ARCH-5c Phase 5e: 列 (opt-in) の delta から merge した超過版数。</param>
public sealed record VacuumReport(
    int ReclaimedNodes,
    int ReclaimedRelationships,
    int ReclaimedProperties,
    int PrunedCommittedTxEntries,
    long ElapsedMs,
    long HorizonTxId,
    bool Skipped,
    long TruncatedPages = 0,
    int ReclaimedColumnVersions = 0);
