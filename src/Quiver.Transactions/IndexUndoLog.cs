using System.Buffers.Binary;
using System.Text;
using Quiver.Core;
using Quiver.Index;
using Quiver.Wal;

namespace Quiver.Transactions;

/// <summary>
/// FT-17: <see cref="WalRecordType.IndexMutation"/> ペイロードのエンコード / デコード。
/// 形式: [op:1][keyKind:1][value:8][nameLen:2][name-utf8:N][keyBytes:M]
/// op は 1=Insert / 0=Delete。
/// </summary>
internal static class IndexMutationCodec
{
    private const int HeaderLength = 12;

    public static byte[] Encode(
        string indexName, IndexKeyKind keyKind, ReadOnlySpan<byte> keyBytes,
        long value, bool isInsert)
    {
        int nameLen = Encoding.UTF8.GetByteCount(indexName);
        var payload = new byte[HeaderLength + nameLen + keyBytes.Length];
        payload[0] = isInsert ? (byte)1 : (byte)0;
        payload[1] = (byte)keyKind;
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(2), value);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(10), (short)nameLen);
        Encoding.UTF8.GetBytes(indexName, payload.AsSpan(HeaderLength, nameLen));
        keyBytes.CopyTo(payload.AsSpan(HeaderLength + nameLen));
        return payload;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out string indexName, out IndexKeyKind keyKind,
        out long value, out bool isInsert, out byte[] keyBytes)
    {
        indexName = string.Empty;
        keyKind = default;
        value = 0;
        isInsert = false;
        keyBytes = Array.Empty<byte>();
        if (payload.Length < HeaderLength) return false;
        isInsert = payload[0] != 0;
        keyKind = (IndexKeyKind)payload[1];
        value = BinaryPrimitives.ReadInt64LittleEndian(payload[2..]);
        int nameLen = BinaryPrimitives.ReadInt16LittleEndian(payload[10..]);
        if (payload.Length < HeaderLength + nameLen) return false;
        indexName = Encoding.UTF8.GetString(payload.Slice(HeaderLength, nameLen));
        keyBytes = payload[(HeaderLength + nameLen)..].ToArray();
        return true;
    }
}

/// <summary>
/// FT-17 → FT-19: 1 書き込みトランザクション分の B+Tree インデックス論理 undo ログ。
/// <see cref="IIndexUndoSink"/> として <see cref="IndexUndoContext"/> に登録され、
/// <see cref="BTreeIndex{TKey}"/> の Insert / Delete 成功を 1 件ずつ受け取る。
///
/// FT-19 以降: 索引ファイルが ARIES page-WAL 対象になり、crash recovery は PageImage redo
/// + CLR undo で完全にカバーされる。よって本クラスは <strong>in-memory 専用</strong>に縮退し、
/// in-process abort の rollback 速度を稼ぐためだけに残置されている (ページ read back を回避)。
/// 過去の <see cref="WalRecordType.IndexMutation"/> WAL 追記は撤去済み (enum 値は古い WAL の
/// 互換性のため予約のまま残し、新規 WAL では発行しない)。
/// </summary>
internal sealed class IndexUndoLog : IIndexUndoSink
{
    private readonly IIndexManager _indexManager;
    private List<Entry>? _buffer;

    public IndexUndoLog(IWriteAheadLog wal, TransactionId txId, IIndexManager indexManager)
    {
        // FT-19: wal / txId 引数は呼び出し側互換のため受け取るが、本クラスではもう使わない。
        _ = wal; _ = txId;
        _indexManager = indexManager;
    }

    /// <summary>本トランザクションが索引ミューテーションを 1 件でも記録したか。</summary>
    public bool HasMutations => _buffer is { Count: > 0 };

    public void RecordIndexMutation(
        string indexName, IndexKeyKind keyKind, ReadOnlySpan<byte> keyBytes,
        long value, bool isInsert)
    {
        byte[] kb = keyBytes.ToArray();
        (_buffer ??= new List<Entry>()).Add(new Entry(indexName, keyKind, kb, value, isInsert));
        // FT-19: crash recovery durability は索引 PagedFile の EnableWalLogging (PageImage / CLR)
        // が担うので、本経路から WalRecordType.IndexMutation を WAL へ追記する必要はない。
        // in-process abort 時に RollBack() が _buffer を逆順に再生して索引状態を巻き戻す。
    }

    /// <summary>
    /// バッファした索引ミューテーションを逆順で逆適用し、本トランザクションの索引変更を
    /// インプロセスで巻き戻す。abort 時に呼ぶこと。呼び出し側は事前に
    /// <see cref="IndexUndoContext.End()"/> を呼び、逆適用が新たな undo を記録しないように
    /// しておく必要がある。
    /// </summary>
    public void RollBack()
    {
        if (_buffer == null) return;
        for (int i = _buffer.Count - 1; i >= 0; i--)
        {
            var e = _buffer[i];
            // 逆操作: Insert → Delete, Delete → Insert。
            _indexManager.ApplyEncodedIndexMutation(
                e.Name, e.Kind, e.KeyBytes, e.Value, isInsert: !e.IsInsert);
        }
        _buffer.Clear();
    }

    private readonly record struct Entry(
        string Name, IndexKeyKind Kind, byte[] KeyBytes, long Value, bool IsInsert);
}
