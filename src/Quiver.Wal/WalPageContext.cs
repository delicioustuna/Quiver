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
