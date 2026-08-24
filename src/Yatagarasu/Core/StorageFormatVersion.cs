namespace Yatagarasu.Core;

/// <summary><c>QUIVER-SW</c> database family のフォーマットバージョン。</summary>
internal static class StorageFormatVersion
{
    /// <summary>checkpoint catalog と page-LSN WAL を持つ形式。</summary>
    public const byte Current = 2;
}

/// <summary>
/// 期待しないフォーマットバージョンの DB を open したときに throw する。
/// 自動マイグレーションは提供しないため、旧 format の DB は新規作成し直す必要がある。
/// </summary>
public sealed class StorageFormatMismatchException : YatagarasuException
{
    /// <summary>ファイルに記録されていたフォーマットバージョン。</summary>
    public byte Found { get; }

    /// <summary>このビルドが要求するフォーマットバージョン。</summary>
    public byte Expected { get; }

    /// <summary>不一致が検出されたファイル種別の名称。</summary>
    public string FileKind { get; }

    /// <summary>ファイル種別、検出バージョン、期待バージョンを指定して例外を生成する。</summary>
    public StorageFormatMismatchException(string fileKind, byte found, byte expected)
        : base($"Storage format mismatch on {fileKind}: file is family version {found}, " +
               $"this build requires QUIVER-SW family version {expected}. " +
               "Recreate the database from source data or a logical export.")
    {
        FileKind = fileKind;
        Found = found;
        Expected = expected;
    }
}

/// <summary>期待しない WAL family または version を検出したときに送出する。</summary>
public sealed class WalFormatMismatchException : YatagarasuException
{
    /// <summary>検出した WAL family の説明。</summary>
    public string Found { get; }

    /// <summary>期待する WAL family の説明。</summary>
    public string Expected { get; }

    /// <summary>検出値と期待値を指定して例外を生成する。</summary>
    public WalFormatMismatchException(string found, string expected)
        : base($"WAL format mismatch: found {found}, expected {expected}. " +
               "This Yatagarasu build does not read WAL files from another format family.")
    {
        Found = found;
        Expected = expected;
    }
}
