using FluentAssertions;
using Quiver.Core;
using Quiver.Index.FullText;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// FTS-2 (increment 4): the orphan sweep safety net. Postings/norms whose entityId
/// (decoded from the key, not the value) points at a non-live node are detected by
/// CheckIndexConsistency and removed by RepairIndexes — without touching live docs.
/// Transactional maintenance already keeps things consistent; this is the belt-and-
/// suspenders path (design 13 section 4).
/// </summary>
public sealed class FullTextOrphanSweepTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public FullTextOrphanSweepTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_fts2_orphan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "graph.quiver");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Repair_removes_postings_and_norms_for_dead_entities_only()
    {
        using var db = GraphDatabase.Open(_path);
        db.Schema.CreateFullTextIndex("idx_body", "Doc", "body");

        var mgr = ((SchemaApi)db.Schema).IndexManager;
        mgr.TryGetFullTextIndex("idx_body", out var ft).Should().BeTrue();
        var tok = mgr.ResolveTokenizer(ft.TokenizerId);

        // Inject an orphan: a packed ref to a node sequence that was never allocated.
        long deadEntity = EntityRef.Pack(EntityKind.Node, 99_999, 0);
        ft.AddDocument(deadEntity, tok, "orphan term");

        // A real, live document that must survive the sweep.
        using (var tx = db.BeginTransaction())
        {
            var live = tx.CreateNode("Doc");
            tx.SetProperty(live, "body", PropertyValue.FromString("kept term"));
            tx.Commit();
        }

        ft.DocumentCount.Should().Be(2);

        var report = db.Diagnostics.CheckIndexConsistency();
        report.OrphanCount.Should().BeGreaterThan(0);
        // The public report decodes the entityId from the key (not the tf/docLen value)
        // and strips the lane tag from the index name.
        report.Orphans.Should().Contain(o => o.IndexName == "idx_body");

        var repair = db.Diagnostics.RepairIndexes(IndexRepairMode.Apply);
        repair.RemovedCount.Should().BeGreaterThan(0);

        ft.GetPostings("orphan").Should().BeEmpty();
        ft.TryGetDocLength(deadEntity, out _).Should().BeFalse();
        // Live document untouched.
        ft.GetPostings("kept").Should().ContainSingle();
        ft.DocumentCount.Should().Be(1);

        // Re-check: no orphans remain.
        db.Diagnostics.CheckIndexConsistency().OrphanCount.Should().Be(0);
    }
}
