namespace Quiver.Migrations;

/// <summary>
/// 宣言的スキーママイグレーション。EF Core 風に <see cref="Version"/> の昇順で適用される。
/// </summary>
/// <remarks>
/// 各マイグレーションは <see cref="GraphDatabase.MigrateAsync"/> に渡され、未適用のものだけが
/// 順に <see cref="ApplyAsync"/> される。同じ <see cref="Id"/> は 1 度しか適用されないため、
/// 再実行 (アプリ再起動 / 障害復旧後) でも冪等。失敗時はトランザクションが rollback され、
/// migration history にも登録されない (= 次回起動時に再度 Apply が試みられる)。
/// 注意: ラベル / プロパティキー / 索引のリネームは <see cref="ITokenStore{TToken}"/> / 索引ファイル
/// の物理操作で、データミューテーションと違いトランザクション境界では巻き戻らない。
/// よって rename を含む migration が途中で失敗すると名前空間は中途半端な状態で残る。
/// rename は <see cref="ApplyAsync"/> の最初に置き、データミューテーションをその後に置く構成を推奨する。
/// </remarks>
public interface IMigration
{
    /// <summary>マイグレーション一意 ID。history テーブルでの重複検出に使う。</summary>
    string Id { get; }

    /// <summary>適用順序。昇順に sort される。同 Version は <see cref="Id"/> の Ordinal 順。</summary>
    int Version { get; }

    /// <summary>前進方向の適用。</summary>
    Task ApplyAsync(IMigrationContext ctx);

    /// <summary>
    /// 巻き戻し。未実装で <see cref="NotSupportedException"/> を投げてもよい
    /// (Quiver 自体は revert を自動実行しない。運用者が手動で呼ぶ場合の置き場)。
    /// </summary>
    Task RevertAsync(IMigrationContext ctx) => throw new NotSupportedException(
        $"Migration '{Id}' does not implement RevertAsync.");
}
