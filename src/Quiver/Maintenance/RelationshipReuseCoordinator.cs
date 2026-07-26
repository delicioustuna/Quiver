using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;

namespace Quiver.Maintenance;

internal enum RelationshipReusePhase : byte
{
    None = 0,
    CandidatesDurable = 1,
    DerivedDurable = 2,
    ReleaseReady = 3,
}

/// <summary>
/// raw Sequence を保持する導出データが新しい base へ収束するまで、Edge sequence の
/// free list 解放を保留する。phase と候補は専用テナントへ永続化し、再 open 時に再開する。
/// </summary>
internal sealed class RelationshipReuseCoordinator
{
    private const uint Magic = 0x53555251; // "QRUS"
    private const byte Version = 1;
    private static readonly PageId HeaderPage = new(1);
    private const int SequencesPerPage = RecordPageMapping.PageBodySize / sizeof(long);

    internal static Action<RelationshipReusePhase>? PhasePersistedForTest;

    private readonly IPagedFile _file;
    private readonly VersionedEdgeStore _edges;
    private readonly Action _rebuildDerived;
    private readonly Action _flush;

    internal RelationshipReuseCoordinator(
        IPagedFile file,
        VersionedEdgeStore edges,
        Action rebuildDerived,
        Action flush)
    {
        _file = file;
        _edges = edges;
        _rebuildDerived = rebuildDerived;
        _flush = flush;
    }

    internal void BeginAndRun(IReadOnlyCollection<long> candidates)
    {
        if (candidates.Count == 0) return;
        Persist(RelationshipReusePhase.CandidatesDurable, candidates);
        Resume();
    }

    internal void Resume()
    {
        (RelationshipReusePhase phase, long[] candidates) = Read();
        if (phase == RelationshipReusePhase.None || candidates.Length == 0)
            return;

        if (phase == RelationshipReusePhase.CandidatesDurable)
        {
            _rebuildDerived();
            Persist(RelationshipReusePhase.DerivedDurable, candidates);
            phase = RelationshipReusePhase.DerivedDurable;
        }

        if (phase == RelationshipReusePhase.DerivedDurable)
        {
            Persist(RelationshipReusePhase.ReleaseReady, candidates);
            phase = RelationshipReusePhase.ReleaseReady;
        }

        if (phase == RelationshipReusePhase.ReleaseReady)
        {
            _edges.ReleaseReclaimedSequences(candidates);
            _flush();
            Persist(RelationshipReusePhase.None, []);
        }
    }

    private (RelationshipReusePhase Phase, long[] Candidates) Read()
    {
        if (_file.PageCount <= HeaderPage.Value)
            return (RelationshipReusePhase.None, []);

        int count;
        RelationshipReusePhase phase;
        using (var header = _file.PinForRead(HeaderPage))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.Data) != Magic)
                return (RelationshipReusePhase.None, []);
            byte version = header.Data[4];
            if (version != Version)
                throw new StorageFormatMismatchException(
                    "relationship-reuse", version, Version);
            phase = (RelationshipReusePhase)header.Data[5];
            count = BinaryPrimitives.ReadInt32LittleEndian(header.Data[8..]);
        }

        var result = new long[count];
        int index = 0;
        for (long page = 2; index < count; page++)
        {
            using var data = _file.PinForRead(new PageId(page));
            int take = Math.Min(SequencesPerPage, count - index);
            for (int i = 0; i < take; i++)
            {
                result[index++] = BinaryPrimitives.ReadInt64LittleEndian(
                    data.Data[(i * sizeof(long))..]);
            }
        }
        return (phase, result);
    }

    private void Persist(
        RelationshipReusePhase phase,
        IReadOnlyCollection<long> candidates)
    {
        while (_file.PageCount <= HeaderPage.Value)
            _file.AllocatePage(PageKind.Header);

        long[] ordered = [.. candidates.Order()];
        int requiredPages = (ordered.Length + SequencesPerPage - 1) / SequencesPerPage;
        while (_file.PageCount < 2 + requiredPages)
            _file.AllocatePage(PageKind.Header);

        using (var header = _file.PinForWrite(HeaderPage))
        {
            header.Data[..12].Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(header.Data, Magic);
            header.Data[4] = Version;
            header.Data[5] = (byte)phase;
            BinaryPrimitives.WriteInt32LittleEndian(header.Data[8..], ordered.Length);
        }

        int index = 0;
        for (long page = 2; index < ordered.Length; page++)
        {
            using var data = _file.PinForWrite(new PageId(page));
            data.Data.Clear();
            int take = Math.Min(SequencesPerPage, ordered.Length - index);
            for (int i = 0; i < take; i++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(
                    data.Data[(i * sizeof(long))..],
                    ordered[index++]);
            }
        }

        _flush();
        PhasePersistedForTest?.Invoke(phase);
    }
}
