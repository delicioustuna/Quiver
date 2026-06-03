namespace Quiver.Core;

/// <summary>
/// FT-26: ストアファイルのフォーマットバージョン。MVCC 対応 (v2) は record header に
/// xmin / xmax 8B 各を追加するため、旧 v1 とはバイト配置が非互換 (record サイズが拡大)。
/// develop 段階のためマイグレーションは提供せず、open 時に v1 を検出したら例外。
/// </summary>
internal static class FormatVersion
{
    /// <summary>v1: FT-15 までのレイアウト (xmin/xmax 無し)。FT-26 より開けない。</summary>
    public const byte V1 = 1;

    /// <summary>v2: FT-26 MVCC レイアウト。record header に xmin/xmax を持つ。FT-32 より開けない。</summary>
    public const byte V2Mvcc = 2;

    /// <summary>
    /// v3: FT-32 MVCC sidecar レイアウト。record から xmin/xmax を撤去し、EntityKind 別の
    /// sidecar (<see cref="Quiver.Wal.WalFileKind.NodeVersionMeta"/> 等) に移管した。record が縮み
    /// (Node 31→15B / Rel 64→48B / Prop 57→41B)、cache line residency が改善する。v2 とはバイト配置が
    /// 非互換 (record サイズが縮小し、xmin/xmax が別ファイルに移る)。ARCH-3 より開けない。
    /// </summary>
    public const byte V3MvccSidecar = 3;

    /// <summary>
    /// v4: ARCH-3 索引 Generation レイアウト。EntityVersionMeta sidecar に slot incarnation を表す
    /// Generation レーンを追加し (entry 32→40B)、B+Tree 索引の値レーンを
    /// <see cref="EntityRef"/> (Kind/Generation/Sequence) でパックする。slot 再利用に伴う stale
    /// 索引エントリ (ABA) を解決時の世代照合で弾けるようにする。v3 とは sidecar entry サイズが
    /// 非互換。
    /// </summary>
    public const byte V4IndexGeneration = 4;

    /// <summary>
    /// v5: ARCH-4 単一ファイル化。コア store / version sidecar / token / 索引 / 隣接ブロック / epoch を
    /// すべて単一 <c>*.quiver</c> コンテナ (<see cref="Quiver.Storage.SingleFileContainer"/>) のテナント
    /// として同居させ、WAL を単一サイドカー <c>*.quiver-wal</c> へ一本化、クリーン終了で WAL を削除する。
    /// コンテナのカタログ root に committed TxId 高水位フィールドを追加 (記述子オフセット変更) しており、
    /// v4 (索引 / adjacency / token / WAL がサイドカー群、カタログ記述子 offset 16) とは
    /// レイアウト非互換。develop 段階のためマイグレーションは提供しない。
    /// </summary>
    public const byte V5SingleFile = 5;

    /// <summary>現行 (= 新規 DB を作成するときに書き込むバージョン)。</summary>
    public const byte Current = V5SingleFile;
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
