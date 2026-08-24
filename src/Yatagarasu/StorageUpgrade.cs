using Yatagarasu.Core;
using Yatagarasu.Storage.Upgrade;

namespace Yatagarasu;

/// <summary>ストレージ形式の移行結果を表す状態。</summary>
public enum StorageUpgradeStatus
{
    /// <summary>データベースは既に現行形式であり、ファイルを変更しなかった。</summary>
    AlreadyCurrent = 0,

    /// <summary>登録済みの移行手順で現行形式へ移行した。</summary>
    Upgraded = 1,
}

/// <summary>オフラインのストレージ形式移行に使うオプション。</summary>
public sealed class StorageUpgradeOptions
{
    /// <summary>
    /// 移行前のデータベースをバックアップとして残すかどうか。既定値は <c>true</c>。
    /// </summary>
    public bool KeepBackup { get; set; } = true;

    /// <summary>
    /// 移行前バックアップのパス。<c>null</c> の場合は source と同じディレクトリに
    /// Yatagarasu が名前を割り当てる。原子的な切替のため source と同じディレクトリでなければならない。
    /// </summary>
    public string? BackupFilePath { get; set; }

    /// <summary>移行処理のキャンセルを通知するトークン。</summary>
    public CancellationToken CancellationToken { get; set; }
}

/// <summary>ストレージ形式移行の実行結果。</summary>
/// <param name="Status">移行したか、既に現行形式だったか。</param>
/// <param name="SourceVersion">呼び出し時に検出した QUIVER-SW family version。</param>
/// <param name="TargetVersion">この Yatagarasu build が使用する QUIVER-SW family version。</param>
/// <param name="BackupFilePath">保持した移行前バックアップのパス。保持しなかった場合は <c>null</c>。</param>
public sealed record StorageUpgradeResult(
    StorageUpgradeStatus Status,
    byte SourceVersion,
    byte TargetVersion,
    string? BackupFilePath);

/// <summary>
/// 検出したストレージ形式から現行形式までの移行手順が登録されていない場合に送出する。
/// </summary>
public sealed class StorageUpgradeNotSupportedException : YatagarasuException
{
    /// <summary>移行元の QUIVER-SW family version。</summary>
    public byte SourceVersion { get; }

    /// <summary>移行先の QUIVER-SW family version。</summary>
    public byte TargetVersion { get; }

    /// <summary>移行元と移行先の family version を指定して例外を生成する。</summary>
    /// <param name="sourceVersion">移行元の family version。</param>
    /// <param name="targetVersion">移行先の family version。</param>
    public StorageUpgradeNotSupportedException(byte sourceVersion, byte targetVersion)
        : base(
            $"Storage upgrade from QUIVER-SW family version {sourceVersion} " +
            $"to version {targetVersion} is not supported by this build.")
    {
        SourceVersion = sourceVersion;
        TargetVersion = targetVersion;
    }
}
