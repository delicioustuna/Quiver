namespace Quiver.Core;

/// <summary>
/// FT-26: ストアファイルのフォーマットバージョン。MVCC 対応 (v2) は record header に
/// xmin / xmax 8B 各を追加するため、旧 v1 とはバイト配置が非互換 (record サイズが拡大)。
/// develop 段階のためマイグレーションは提供せず、open 時に v1 を検出したら例外。
/// </summary>
public static class FormatVersion
{
    /// <summary>v1: FT-15 までのレイアウト (xmin/xmax 無し)。FT-26 より開けない。</summary>
    public const byte V1 = 1;

    /// <summary>v2: FT-26 MVCC レイアウト。record header に xmin/xmax を持つ。</summary>
    public const byte V2Mvcc = 2;

    /// <summary>現行 (= 新規 DB を作成するときに書き込むバージョン)。</summary>
    public const byte Current = V2Mvcc;
}

/// <summary>
/// FT-26: 期待しないフォーマットバージョンの DB を open したときに throw する。
/// develop 段階で v1 → v2 への自動マイグレーションを提供しないため、旧 DB は新規作成し直す必要がある。
/// </summary>
public sealed class FormatVersionMismatchException : GraphDbException
{
    public byte Found { get; }
    public byte Expected { get; }
    public string FileKind { get; }

    public FormatVersionMismatchException(string fileKind, byte found, byte expected)
        : base($"Format version mismatch on {fileKind}: file is v{found}, this build requires v{expected}. " +
               "Pre-release breaking change (FT-26 MVCC). Recreate the database from source data.")
    {
        FileKind = fileKind;
        Found = found;
        Expected = expected;
    }
}
