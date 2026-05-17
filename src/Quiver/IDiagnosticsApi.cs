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
}

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
