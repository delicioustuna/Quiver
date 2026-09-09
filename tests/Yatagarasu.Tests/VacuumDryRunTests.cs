using System.Reflection;
using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Maintenance;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;
using Yatagarasu.Storage.Wal;
using Yatagarasu.Transactions;
using Xunit;

namespace Yatagarasu.Tests;

[Collection("binary-backend-maintenance")]
public sealed class VacuumDryRunTests
{
    [Theory]
    [InlineData(VacuumTarget.None)]
    [InlineData(VacuumTarget.Vertices)]
    [InlineData(VacuumTarget.Edges)]
    [InlineData(VacuumTarget.Nexuses)]
    [InlineData(VacuumTarget.Properties)]
    [InlineData(VacuumTarget.Indexes)]
    [InlineData(VacuumTarget.All)]
    public void Dry_run_preserves_persisted_bytes_wal_and_cached_pages(VacuumTarget targets)
    {
        string directory = Path.Combine(Path.GetTempPath(), "yatagarasu_dry_run_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "graph.yata");
        try
        {
            using var db = YatagarasuDatabase.Open(path);
            VertexId owner;
            using (var tx = db.BeginWriteTransaction())
            {
                var a = tx.CreateVertex("A");
                var b = tx.CreateVertex("B");
                owner = tx.CreateVertex("Owner");
                var edge = tx.CreateEdge(a, b, "Link");
                var nexus = tx.CreateNexus("Fact", [new("a", a), new("b", b)]);
                tx.SetProperty(owner, "value", PropertyValue.FromFloatArray(new float[] { 1, 2, 3 }));
                tx.SetProperty(edge, "value", PropertyValue.FromString(new string('x', 300)));
                tx.SetProperty(nexus, "value", PropertyValue.FromInt32(42));
                tx.DeleteEdge(edge);
                tx.DeleteNexus(nexus);
                tx.DeleteVertex(owner);
                tx.Commit();
            }
            var backend = db.BackendInternal;
            var container = Field<object>(backend, "_container");
            var physical = (IPagedFile)container.GetType().GetProperty("Physical", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(container)!;
            var stream = Field<FileStream>(physical, "_fileStream");
            var wal = Field<IWriteAheadLog>(backend, "_wal");
            var manager = Field<TransactionManager>(backend, "_txManager");
            byte[][] pages = CachedPages(physical);
            byte[] persisted = DiskBytes(stream);
            byte[] walBytes = SharedBytes(path + "-wal");
            var state = (wal.CurrentLsn, wal.FlushedLsn, manager.CommittedRegistry.Count,
                manager.CommittedRegistry.CompactedVisibilityHorizon);
            var dry = db.Vacuum(new VacuumOptions { Mode = VacuumMode.DryRun, Targets = targets });
            db.Vacuum(new VacuumOptions { Mode = VacuumMode.DryRun, Targets = targets })
                .Should().BeEquivalentTo(dry, config => config.Excluding(x => x.ElapsedMs));
            DiskBytes(stream).Should().Equal(persisted);
            SharedBytes(path + "-wal").Should().Equal(walBytes);
            (wal.CurrentLsn, wal.FlushedLsn, manager.CommittedRegistry.Count,
                manager.CommittedRegistry.CompactedVisibilityHorizon).Should().Be(state);
            CachedPages(physical).Should().BeEquivalentTo(pages, config => config.WithStrictOrdering());
            var full = db.Vacuum(new VacuumOptions { Targets = targets });
            dry.Should().BeEquivalentTo(full, config => config.Excluding(x => x.ElapsedMs));
            if (targets == VacuumTarget.All)
            {
                full.ReclaimedVertices.Should().Be(1);
                full.ReclaimedEdges.Should().Be(1);
                full.ReclaimedNexuses.Should().Be(1);
                full.ReclaimedIncidences.Should().Be(2);
                full.ReclaimedProperties.Should().Be(3);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static byte[][] CachedPages(IPagedFile file) => Enumerable.Range(0, (int)file.PageCount).Select(i =>
    {
        using var page = file.PinForRead(new PageId(i));
        return page.Raw.ToArray();
    }).ToArray();

    private static byte[] SharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return DiskBytes(stream);
    }

    private static byte[] DiskBytes(FileStream stream)
    {
        byte[] bytes = new byte[checked((int)RandomAccess.GetLength(stream.SafeFileHandle))];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = RandomAccess.Read(stream.SafeFileHandle, bytes.AsSpan(offset), offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        return bytes;
    }
}
