using Quiver.Backend.Tests.Faults;
using Quiver.Transactions;

namespace Quiver.Backend.Tests.Chaos;

/// <summary>
/// binary backend 用の <see cref="IFaultInjector"/> 実装。
/// 既存の <see cref="TornWriteInjector"/> / <see cref="ChecksumCorruptor"/> /
/// <see cref="SidecarFileDeleter"/> をシナリオ runner から呼べる形にラップする。
/// </summary>
internal sealed class BinaryBackendFaultInjector(string databaseDirectory) : IFaultInjector
{
    private readonly string _dir = databaseDirectory;

    public void InjectTornWriteAtTail(int tailBytes)
    {
        var seg = LatestWalSegment();
        if (seg is null) return;
        TornWriteInjector.ZeroFillTail(seg, tailBytes);
    }

    public void InjectChecksumCorruption()
    {
        var seg = LatestWalSegment();
        if (seg is null) return;
        // WAL ヘッダ: Length(4) + Lsn(8) + TxId(8) + Type(1) + Crc32(4) = 25 B。
        // 既存 BinaryGraphStorageBackendCrashContractTests と同様、Type バイトを反転させて
        // CRC を確実に外す。
        ChecksumCorruptor.FlipBitAt(seg, offset: 20, bitInByte: 0);
    }

    public void DeleteSidecarFile()
    {
        var epoch = Path.Combine(_dir, "adj.epoch");
        SidecarFileDeleter.TryDelete(epoch);
    }

    public IDisposable ArmCheckpointPhaseKill(CheckpointPhase phase)
    {
        bool fired = false;
        Checkpointer.PhaseInjector = current =>
        {
            if (!fired && current == phase)
            {
                fired = true;
                throw new InvalidOperationException(
                    $"TS-4 simulated kill at checkpoint phase {phase}");
            }
        };
        return new Disarm();
    }

    private string? LatestWalSegment()
    {
        // WAL は単一サイドカー graph.quiver-wal。
        var walPath = Path.Combine(_dir, "graph.quiver-wal");
        return File.Exists(walPath) ? walPath : null;
    }

    private sealed class Disarm : IDisposable
    {
        public void Dispose() => Checkpointer.PhaseInjector = null;
    }
}
