using Quiver.Core;
using Quiver.Storage.Wal;

namespace Quiver.Storage;

/// <summary>
/// 単一データファイルのページ単位アクセスを提供する。
/// </summary>
internal interface IPagedFile : IDisposable
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

    /// <summary>
    /// FTS-7 (design 13 §10.3): 指定の WAL journaling モードで書き込み用にページを pin する。
    /// WAL を持たない実装は <paramref name="mode"/> を無視してよい (既定は mode を無視して通常 pin)。
    /// </summary>
    PageWriteHandle PinForWrite(PageId pageId, WalJournalMode mode) => PinForWrite(pageId);

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

    /// <summary>
    /// OP-5: ページファイルを <paramref name="newPageCount"/> へ物理 truncate する。
    /// バッファプール上で newPageCount 以上のページキャッシュを drop し、MMF を unmap、
    /// <c>SetLength</c> 後に remap、メタページの PageCount を新しい値で書き戻す。
    /// 呼び出し側は事前に <see cref="WalRecordType.FileTruncate"/> を WAL に書いて durable 化し、
    /// アクティブ tx が 0 であることを保証する。recovery 中の冪等再生にも使われる。
    /// 既定実装は <see cref="NotSupportedException"/> を投げる。
    /// </summary>
    void Truncate(long newPageCount) => throw new NotSupportedException();
}
