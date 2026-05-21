using System.Buffers.Binary;
using Quiver.Core;

namespace Quiver.Wal;

/// <summary>
/// 書き込みトランザクション中のページイメージロギング用に使われる、スレッドローカルな
/// WAL コンテキスト。書き込みトランザクション開始時にセットし、Commit / Abort 時にクリアする。
/// </summary>
public static class WalPageContext
{
    [ThreadStatic]
    internal static WriteTransactionContext? Current;

    /// <summary>このスレッドで書き込みトランザクションを開始する。</summary>
    public static void Begin(IWriteAheadLog wal, TransactionId txId)
        => Current = new WriteTransactionContext(wal, txId);

    /// <summary>現在の書き込みコンテキストを破棄する。</summary>
    public static void End() => Current = null;

    /// <summary>
    /// このスレッドで書き込みトランザクションがアクティブな場合、PageImage を
    /// トランザクションごとのバッファに記録する (案C: コアレス)。同じ (fileKind, pageId)
    /// を複数回触っても、保持されるのは最新のページ内容 1 件のみ。WAL への実際の追記は
    /// <see cref="FlushPending"/> (コミット時) まで遅延される。
    /// </summary>
    /// <returns>遅延ロギングのため LSN はまだ確定しない。常に -1 を返す。</returns>
    public static long LogPageImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
        => Current is { } ctx ? ctx.LogPageImage(fileKind, pageId, pageBytes) : -1L;

    /// <summary>
    /// FT-15: あるページが本トランザクション内で初めて書き込み用に pin された時点の
    /// 内容 (before-image) を記録する。書き込みトランザクション未アクティブ時は no-op。
    /// 同一ページの 2 回目以降の pin は無視される (初回タッチのみ保持)。
    /// </summary>
    public static void CaptureBeforeImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
        => Current?.CaptureBeforeImage(fileKind, pageId, pageBytes);

    /// <summary>
    /// FT-15: 現在の書き込みトランザクションがキャプチャした before-image (CLR ペイロード)
    /// を列挙する。インプロセス abort の巻き戻しに使う。コンテキスト未設定時は空。
    /// </summary>
    public static IReadOnlyCollection<byte[]> CurrentBeforeImagePayloads
        => Current?.BeforeImagePayloads ?? Array.Empty<byte[]>();

    /// <summary>
    /// 現在の書き込みトランザクションがバッファした PageImage をすべて WAL へ追記する。
    /// コミット時に <c>Commit</c> レコードを書く直前に呼ぶこと。コンテキスト未設定時は no-op。
    /// </summary>
    public static void FlushPending() => Current?.FlushPending();
}

internal sealed class WriteTransactionContext(IWriteAheadLog wal, TransactionId txId)
{
    private readonly IWriteAheadLog _wal = wal;
    private readonly TransactionId _txId = txId;

    // (fileKind, pageId) → エンコード済み PageImage ペイロード。
    // 案C: 1 トランザクション中に同一ページを何度触っても、コミット時に最新版 1 件だけを
    // WAL に書く。これにより FlushMeta() 等によるホットページの再ログ増幅を解消する。
    private readonly Dictionary<(byte FileKind, long PageId), byte[]> _pending = new();

    // FT-15: (fileKind, pageId) → エンコード済み before-image (CLR) ペイロード。
    // ページが本トランザクションで「最初に」ダーティ化される直前の内容を 1 枚だけ保持する。
    // 値は CompensationLogRecord ペイロードそのものなので、WAL 追記とインプロセス abort の
    // 巻き戻しで同じバッファを共有できる。
    private readonly Dictionary<(byte FileKind, long PageId), byte[]> _beforeImages = new();

    /// <summary>FT-15: キャプチャ済み before-image (CLR ペイロード) のコレクション。</summary>
    public IReadOnlyCollection<byte[]> BeforeImagePayloads => _beforeImages.Values;

    /// <summary>
    /// PageImage をトランザクションバッファに記録 (または上書き) する。
    /// ペイロード形式: [version=1:1][fileKind:1][pageId:8][pageBytes:N]
    /// </summary>
    public long LogPageImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        var key = (fileKind, pageId);
        int payloadLen = 10 + pageBytes.Length;
        // 同一ページは常に同じページサイズなのでバッファを再利用し、最新内容で上書きする。
        if (!_pending.TryGetValue(key, out var payload) || payload.Length != payloadLen)
        {
            payload = new byte[payloadLen];
            payload[0] = 1;
            payload[1] = fileKind;
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(2), pageId);
            _pending[key] = payload;
        }
        pageBytes.CopyTo(payload.AsSpan(10));
        return -1L;
    }

    /// <summary>
    /// FT-15: ページの before-image を初回タッチ時に 1 度だけ捕捉する。
    /// 捕捉した内容は (1) インプロセス abort の巻き戻し用にバッファされ、
    /// (2) <see cref="WalRecordType.CompensationLogRecord"/> として WAL へ即時追記され、
    /// クラッシュ recovery の undo パスで使われる。2 回目以降の同一ページ pin は無視する。
    ///
    /// 案C の after-image バッファ (<see cref="_pending"/>) と異なり、CLR は遅延せず
    /// 即時追記する: コミットも abort もせずクラッシュしたトランザクションでも、
    /// before-image が WAL に残っていなければ巻き戻せないため。1 トランザクション内で
    /// 同一ページにつき 1 件しか書かないので WAL 肥大には繋がらない。
    /// </summary>
    public void CaptureBeforeImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        var key = (fileKind, pageId);
        if (_beforeImages.ContainsKey(key)) return; // 初回タッチのみ
        byte[] payload = WalPageImageCodec.Encode(fileKind, pageId, pageBytes);
        _beforeImages[key] = payload;
        _wal.Append(WalRecordType.CompensationLogRecord, _txId, payload);
    }

    /// <summary>
    /// バッファした PageImage をすべて WAL へ追記し、バッファをクリアする。
    /// アボート時は呼ばれず、バッファは <see cref="WalPageContext.End"/> による
    /// コンテキスト破棄とともに破棄される (アボートしたトランザクションのページは WAL に残らない)。
    /// </summary>
    public void FlushPending()
    {
        if (_pending.Count == 0) return;
        foreach (var payload in _pending.Values)
            _wal.Append(WalRecordType.PageImage, _txId, payload);
        _pending.Clear();
    }
}
