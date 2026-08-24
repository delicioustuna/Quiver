namespace Yatagarasu.Migrations;

/// <summary>primary catalog に保存された適用済みマイグレーション 1 件の記録。</summary>
/// <param name="Id">データベース内で一意なマイグレーション ID。</param>
/// <param name="Version">適用順の決定に使うバージョン。</param>
/// <param name="AppliedAtUtc">適用トランザクションを開始した UTC 時刻。</param>
public sealed record MigrationHistoryEntry(string Id, int Version, DateTime AppliedAtUtc);
