namespace Quiver.Core;

/// <summary>
/// ストアファイルのフォーマットバージョン。
/// v1 は Quiver 1.0 候補のベースライン format。未リリース期間中に重ねた format 履歴
/// (pre-MVCC → MVCC → sidecar → 単一ファイル → columnar → vector → 全文 → logical WAL) は
/// クリーンブレイクで畳み、現実装を v1 として再宣言した。
/// v2 は vector catalog を長さプレフィクス付きの自己記述エントリへ変更し、HNSW の
/// on-disk レイアウトパラメタを index ごとに永続化する。
/// v3 は第一級ハイパーエッジ用の固定テナント、ID kind、type / role token 空間を追加する。
/// 自動マイグレーションは提供しないため、旧 format の DB は open 時に reject される。
/// </summary>
internal static class FormatVersion
{
    /// <summary>v1: Quiver 1.0 ベースライン format。</summary>
    public const byte V1 = 1;

    /// <summary>v2: 自己記述 vector catalog と per-index HNSW レイアウト。</summary>
    public const byte V2 = 2;

    /// <summary>v3: 第一級ハイパーエッジの ID / token / tenant 基盤。</summary>
    public const byte V3 = 3;

    /// <summary>現行 (= 新規 DB を作成するときに書き込むバージョン)。</summary>
    public const byte Current = V3;
}

/// <summary>
/// 期待しないフォーマットバージョンの DB を open したときに throw する。
/// 自動マイグレーションは提供しないため、旧 format の DB は新規作成し直す必要がある。
/// </summary>
public sealed class FormatVersionMismatchException : GraphDbException
{
    /// <summary>ファイルに記録されていたフォーマットバージョン。</summary>
    public byte Found { get; }
    /// <summary>このビルドが要求するフォーマットバージョン。</summary>
    public byte Expected { get; }
    /// <summary>不一致が検出されたファイル種別の名称。</summary>
    public string FileKind { get; }

    /// <summary>ファイル種別 / 検出バージョン / 期待バージョンを指定して例外を生成する。</summary>
    public FormatVersionMismatchException(string fileKind, byte found, byte expected)
        : base($"Format version mismatch on {fileKind}: file is v{found}, this build requires v{expected}. " +
               "This Quiver build does not migrate older on-disk formats; recreate the database from source data.")
    {
        FileKind = fileKind;
        Found = found;
        Expected = expected;
    }
}
