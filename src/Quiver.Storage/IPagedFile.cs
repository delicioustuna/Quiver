using Quiver.Core;
using Quiver.Wal;

namespace Quiver.Storage;

/// <summary>
/// 単一データファイルのページ単位アクセスを提供する。
/// </summary>
public interface IPagedFile : IDisposable
{
    /// <summary>1 ページのサイズ (バイト)。</summary>
    int PageSize { get; }

    /// <summary>論理的に割り当て済みのページ数。</summary>
    long PageCount { get; }

    /// <summary>
    /// OP-1: 本ファイルの実体パス。<see cref="GraphDatabase.CreateSnapshot"/> が
    /// page-by-page コピーの対象ファイル名を解決するために参照する。既定実装は空文字列。
    /// </summary>
    string Path => string.Empty;

    /// <summary>新しいページを割り当ててその <see cref="PageId"/> を返す。</summary>
    PageId AllocatePage(PageKind kind);

    /// <summary>ページを解放する。</summary>
    void FreePage(PageId pageId);

    /// <summary>読み取り用にページを pin する。</summary>
    PageReadHandle PinForRead(PageId pageId);

    /// <summary>書き込み用にページを pin する。</summary>
    PageWriteHandle PinForWrite(PageId pageId);

    /// <summary>ページの pin を解除する (変更なし)。</summary>
    void Unpin(PageId pageId);

    /// <summary>ページの pin を解除し dirty マークと WAL ログ書き込みを行う。</summary>
    void UnpinDirty(PageId pageId, long lsn);

    /// <summary>バッファプールのダーティページをディスクにフラッシュする。</summary>
    void Flush();

    /// <summary>
    /// 指定の fileKind バイトで、本ファイルを WAL ページイメージロギング対象として登録する。
    /// FT-15: <paramref name="wal"/> はダーティページをデータファイルへ書き出す前に
    /// WAL を先行フラッシュ (write-ahead) するために使う。
    /// </summary>
    void EnableWalLogging(byte fileKind, IWriteAheadLog wal);

    /// <summary>
    /// FT-18: WAL 参照のみ配線する (物理 PageImage / before-image は出さない)。
    /// buffer-pool eviction や flush の直前に WAL を write-ahead でフラッシュすることで、
    /// ページが OS-MMF に到達する前に対応する論理ログ (例: IndexMutation) が durable に
    /// なっていることを保証する。索引ファイル用の軽量配線。
    /// </summary>
    void EnableWalFlushOnly(IWriteAheadLog wal) { }

    /// <summary>
    /// 必要に応じてファイルを拡張しつつ、生ページバイト列を直接書き込む。
    /// WAL リプレイ中の RecoveryManager が利用する経路で、バッファプールをバイパスする。
    /// </summary>
    void WritePageForRecovery(PageId pageId, ReadOnlySpan<byte> pageBytes);
}
