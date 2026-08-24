using Yatagarasu.Core;

namespace Yatagarasu.Storage.Wal;

/// <summary>
/// 読み出された WAL レコード。Payload は内部バッファのスナップショット。
/// </summary>
internal readonly struct WalRecord
{
    public long Lsn { get; init; }
    public WalRecordType Type { get; init; }
    public TransactionId TransactionId { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }
}
