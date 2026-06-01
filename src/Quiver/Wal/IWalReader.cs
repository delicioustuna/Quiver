namespace Quiver.Storage.Wal;

/// <summary>
/// WAL の前方読み出し。リカバリ専用。
/// </summary>
internal interface IWalReader : IDisposable
{
    bool TryReadNext(out WalRecord record);
}
