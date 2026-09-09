namespace Yatagarasu.Maintenance;

/// <summary>
/// vacuum 実行ポリシー。
/// </summary>
public enum VacuumPolicy
{
    /// <summary>手動実行のみ (既定)。<see cref="YatagarasuDatabaseOptions.AutoVacuum"/> が false の場合。</summary>
    Manual,
    /// <summary>低頻度のバックグラウンドワーカーで自動実行。</summary>
    Auto,
}

/// <summary>
/// vacuum 実行モード。
/// </summary>
public enum VacuumMode
{
    /// <summary>
    /// 指定対象を走査して visibility horizon を越えた dead version と導出 artifact を物理回収する。
    /// </summary>
    Full,
    /// <summary>
    /// 検出のみ。ファイルには触らない。<see cref="VacuumReport"/> は「もし実行したら何件回収するか」のドライラン結果を返す。
    /// </summary>
    DryRun,
}

/// <summary>
/// vacuum オプション。
/// </summary>
public sealed class VacuumOptions
{
    /// <summary>実行モード。既定 <see cref="VacuumMode.Full"/>。</summary>
    public VacuumMode Mode { get; init; } = VacuumMode.Full;

    /// <summary>
    /// 実行を打ち切る上限時間 (ミリ秒)。<c>0</c> 以下で制限なし。
    /// 目安として、書き込み権限の取得後から各処理段階の境界で確認する。開始済みの段階は中断しない。
    /// 整合性維持に必要な後処理とチェックポイントは期限を越えても完了する。既定0（制限なし）。
    /// </summary>
    public int MaxDurationMs { get; init; }

    /// <summary>
    /// 対象エンティティ種別のビットマスク。
    /// </summary>
    public VacuumTarget Targets { get; init; } = VacuumTarget.All;
}

/// <summary>vacuum 対象エンティティの指定。複数ビット同時指定可。</summary>
[System.Flags]
public enum VacuumTarget
{
    /// <summary>対象なし。</summary>
    None = 0,
    /// <summary>Vertexを対象にする。削除済みVertexの回収に必要な所有プロパティも先に回収する。</summary>
    Vertices = 1 << 0,
    /// <summary>Edgeを対象にする。削除済みEdgeの回収に必要な所有プロパティも先に回収する。</summary>
    Edges = 1 << 1,
    /// <summary>Vertex・Edge・Nexusの古いプロパティ版を対象にする。エンティティ自体は回収しない。</summary>
    Properties = 1 << 2,
    /// <summary>索引を対象にする。</summary>
    Indexes = 1 << 3,
    /// <summary>Nexus (と付随する incidence・プロパティ) を対象にする。</summary>
    Nexuses = 1 << 4,
    /// <summary>すべてのエンティティ種別を対象にする。</summary>
    All = Vertices | Edges | Properties | Indexes | Nexuses,
}

/// <summary>
/// vacuum 実行結果。
/// </summary>
/// <param name="ReclaimedVertices">物理回収した 削除済みVertex版数。</param>
/// <param name="ReclaimedEdges">物理回収した 削除済みEdge版数。</param>
/// <param name="ReclaimedProperties">物理回収した dead プロパティ版数。</param>
/// <param name="PrunedCommittedTxEntries">visibility horizon を下回り削除した committed registry エントリ数。</param>
/// <param name="ElapsedMs">実行に要した時間 (ミリ秒)。</param>
/// <param name="HorizonTxId">この実行で採用した visibility horizon。これ未満の xmax を持つ dead version が回収対象。</param>
/// <param name="Skipped">バックエンドが vacuum を実行しなかったとき <see langword="true"/>。</param>
/// <param name="TruncatedPages">物理 truncate で vertices/edges/props 3 ストア合計から削減したページ数。</param>
/// <param name="ReclaimedNexuses">物理回収した dead Nexus header 数。</param>
/// <param name="ReclaimedIncidences">dead Nexusに付随して回収した incidence 数。</param>
/// <param name="RetiredVectorManifests">snapshot horizon を越えて退役した vector manifest 数。</param>
/// <param name="RetiredFullTextManifests">snapshot horizon を越えて退役した全文 manifest 数。</param>
/// <param name="ReclaimedFullTextArtifacts">committed manifest から未参照となり物理回収した全文 artifact 数。</param>
public sealed record VacuumReport(
    int ReclaimedVertices,
    int ReclaimedEdges,
    int ReclaimedProperties,
    int PrunedCommittedTxEntries,
    long ElapsedMs,
    long HorizonTxId,
    bool Skipped,
    long TruncatedPages = 0,
    int ReclaimedNexuses = 0,
    int ReclaimedIncidences = 0,
    int RetiredVectorManifests = 0,
    int RetiredFullTextManifests = 0,
    int ReclaimedFullTextArtifacts = 0);
