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
    /// このスレッドで書き込みトランザクションがアクティブな場合、PageImage WAL レコードを追記する。
    /// 追記したレコードの WAL LSN を返す。コンテキスト未設定の場合は -1。
    /// </summary>
    public static long LogPageImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
        => Current is { } ctx ? ctx.LogPageImage(fileKind, pageId, pageBytes) : -1L;
}

internal sealed class WriteTransactionContext(IWriteAheadLog wal, TransactionId txId)
{
    private readonly IWriteAheadLog _wal = wal;
    private readonly TransactionId _txId = txId;

    /// <summary>
    /// PageImage WAL レコードをエンコードして追記する。
    /// ペイロード形式: [version=1:1][fileKind:1][pageId:8][pageBytes:N]
    /// </summary>
    public long LogPageImage(byte fileKind, long pageId, ReadOnlySpan<byte> pageBytes)
    {
        var payload = new byte[10 + pageBytes.Length];
        payload[0] = 1;
        payload[1] = fileKind;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(2), pageId);
        pageBytes.CopyTo(payload.AsSpan(10));
        return _wal.Append(WalRecordType.PageImage, _txId, payload);
    }
}
