namespace Quiver;

/// <summary>
/// データベースの統計と整合性チェックを提供する診断 API。
/// <see cref="GraphDatabase.Diagnostics"/> から取得する。
/// </summary>
public interface IDiagnosticsApi
{
    /// <summary>現時点の DB 統計を返す。</summary>
    DatabaseStatistics GetStatistics();

    /// <summary>整合性チェックを実行して結果レポートを返す。</summary>
    ConsistencyReport CheckConsistency();

    /// <summary>
    /// FT-22: 全 B+Tree インデックスを走査し、対応するエンティティが既に解放されている
    /// (orphan な) エントリを検出する。FT-19 で索引も ARIES に乗ったが、
    /// 「base store の delete だけ commit され index entry の削除が未到達」「DeleteNode が
    /// 索引エントリを自動削除しない設計上の前提」などで orphan は依然として生じうるため、
    /// 運用者が任意のタイミングで状態を観測できる経路を提供する。
    /// 既定実装は健全 (orphan 0) を返す — 走査機能を持たない backend (例: SQLite) は
    /// PRAGMA integrity_check が論理的役割を担う。
    /// </summary>
    IndexConsistencyReport CheckIndexConsistency() => new(0, 0, 0, Array.Empty<OrphanIndexEntry>(), 0);

    /// <summary>
    /// FT-22: <see cref="CheckIndexConsistency"/> で検出した orphan を実際に除去する。
    /// <see cref="IndexRepairMode.DryRun"/> を渡すと検出のみで実削除は行わない。
    /// 既定実装は no-op。
    /// </summary>
    IndexRepairReport RepairIndexes(IndexRepairMode mode)
        => new(0, Array.Empty<OrphanIndexEntry>(), false);
}

/// <summary>
/// FT-22: orphan 索引エントリ。<paramref name="EntityId"/> は <see cref="Quiver.Core.NodeId.Value"/>
/// 互換の long。<paramref name="RawKey"/> は索引の生バイト列で、再削除に必要なため複製を保持する。
/// </summary>
public sealed record OrphanIndexEntry(string IndexName, byte[] RawKey, long EntityId);

/// <summary>
/// FT-22: <see cref="IDiagnosticsApi.CheckIndexConsistency"/> の結果。
/// </summary>
/// <param name="IndexCount">検査対象とした B+Tree 索引の本数。</param>
/// <param name="EntryCount">走査した索引エントリの総数。</param>
/// <param name="OrphanCount">そのうち orphan として検出された件数。</param>
/// <param name="Orphans">orphan エントリの一覧 (再現性のためそのまま <see cref="IDiagnosticsApi.RepairIndexes"/> に渡せる)。</param>
/// <param name="LabelIndexOrphanCount">VEC-11 の in-memory <c>LabelNodeIndex</c> 内で観測された
///   解放済みノード ID の件数。<c>RepairIndexes(Apply)</c> 時に index を <c>Invalidate()</c> して
///   次回 lookup で再構築させる。</param>
public sealed record IndexConsistencyReport(
    int IndexCount,
    long EntryCount,
    long OrphanCount,
    IReadOnlyList<OrphanIndexEntry> Orphans,
    long LabelIndexOrphanCount);

/// <summary>FT-22: <see cref="IDiagnosticsApi.RepairIndexes"/> の動作モード。</summary>
public enum IndexRepairMode
{
    /// <summary>検出のみで実削除は行わない。観測用。</summary>
    DryRun,
    /// <summary>検出と削除を両方行う。</summary>
    Apply,
}

/// <summary>
/// FT-22: <see cref="IDiagnosticsApi.RepairIndexes"/> の結果。
/// </summary>
/// <param name="RemovedCount">実削除に成功した B+Tree エントリの件数。<see cref="IndexRepairMode.DryRun"/> 時は 0。</param>
/// <param name="Orphans">検出された orphan エントリの一覧 (Apply 時は削除前のスナップショット)。</param>
/// <param name="LabelIndexInvalidated">VEC-11 の <c>LabelNodeIndex</c> を invalidate したか。</param>
public sealed record IndexRepairReport(
    int RemovedCount,
    IReadOnlyList<OrphanIndexEntry> Orphans,
    bool LabelIndexInvalidated);

/// <summary>
/// <see cref="IDiagnosticsApi.GetStatistics"/> の返却用統計レコード。
/// バッファプールヒット率や WAL サイズなど、運用観測の起点として使う。
/// </summary>
/// <param name="NodeCount">ノード件数。</param>
/// <param name="RelationshipCount">リレーションシップ件数。</param>
/// <param name="PropertyCount">プロパティ件数。</param>
/// <param name="DataFileSize">データファイルの合計サイズ (バイト)。</param>
/// <param name="WalFileSize">WAL ファイルの合計サイズ (バイト)。</param>
/// <param name="BufferPoolHits">バッファプールヒット数。</param>
/// <param name="BufferPoolMisses">バッファプールミス数。</param>
/// <param name="AdjacencyFallbackCount">隣接ブロックフォールバック発生回数。</param>
public sealed record DatabaseStatistics(
    long NodeCount,
    long RelationshipCount,
    long PropertyCount,
    long DataFileSize,
    long WalFileSize,
    long BufferPoolHits,
    long BufferPoolMisses,
    long AdjacencyFallbackCount);

/// <summary>
/// 整合性チェック結果。<see cref="IsConsistent"/> が false の場合、
/// <see cref="Issues"/> に検出された問題の文字列が並ぶ。
/// </summary>
public sealed record ConsistencyReport(
    bool IsConsistent,
    IReadOnlyList<string> Issues);
