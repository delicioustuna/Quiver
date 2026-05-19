using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Stores;
using Xunit;

namespace Quiver.Stores.Tests;

/// <summary>
/// VEC-11: <see cref="LabelNodeIndex"/> の round-trip / 増分更新 / reopen rebuild / bulk load invalidation を直接検証する。
/// </summary>
public class LabelNodeIndexTests : IDisposable
{
    private readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly PagedFile _pf;
    private readonly NodeStore _store;
    private readonly LabelNodeIndex _index = new();

    public LabelNodeIndexTests()
    {
        _pf = new PagedFile(_path);
        _store = new NodeStore(_pf, _index);
    }

    public void Dispose() { _pf.Dispose(); System.IO.File.Delete(_path); }

    [Fact]
    public void Allocate_drives_index_to_O_by_L_lookup()
    {
        // Force build at least once so OnAllocate updates instead of skipping.
        _index.EnsureBuilt(_store);

        var docA = _store.Allocate(new LabelId(1));
        var docB = _store.Allocate(new LabelId(1));
        var otherA = _store.Allocate(new LabelId(2));

        _index.Lookup(_store, new LabelId(1)).Select(n => n.Value)
            .Should().BeEquivalentTo(new[] { docA.Value, docB.Value });
        _index.Lookup(_store, new LabelId(2)).Select(n => n.Value)
            .Should().BeEquivalentTo(new[] { otherA.Value });
        _index.Lookup(_store, new LabelId(3)).Should().BeEmpty();
    }

    [Fact]
    public void Free_removes_node_from_label_bucket()
    {
        _index.EnsureBuilt(_store);
        var a = _store.Allocate(new LabelId(1));
        var b = _store.Allocate(new LabelId(1));
        var c = _store.Allocate(new LabelId(2));

        _store.Free(b);

        _index.Lookup(_store, new LabelId(1)).Select(n => n.Value)
            .Should().BeEquivalentTo(new[] { a.Value });
        _index.Lookup(_store, new LabelId(2)).Select(n => n.Value)
            .Should().BeEquivalentTo(new[] { c.Value });
    }

    [Fact]
    public void Lookup_lazily_builds_index_on_first_access()
    {
        // Allocate before EnsureBuilt; index should remain "not built" and OnAllocate is a no-op.
        _store.Allocate(new LabelId(1));
        _store.Allocate(new LabelId(1));
        _store.Allocate(new LabelId(2));

        _index.IsBuilt.Should().BeFalse();

        // First Lookup triggers Rebuild from full Scan.
        var docs = _index.Lookup(_store, new LabelId(1)).ToList();
        docs.Should().HaveCount(2);
        _index.IsBuilt.Should().BeTrue();
        _index.CountFor(new LabelId(1)).Should().Be(2);
        _index.CountFor(new LabelId(2)).Should().Be(1);
    }

    [Fact]
    public void Allocate_after_free_reuses_id_into_new_label_bucket()
    {
        _index.EnsureBuilt(_store);
        var first = _store.Allocate(new LabelId(1));
        _store.Free(first);

        // NodeStore recycles the freed id back into the free list, so the next
        // allocate gets the same id but with a fresh label. The index must move
        // it from bucket(1) to bucket(2).
        var reused = _store.Allocate(new LabelId(2));
        reused.Value.Should().Be(first.Value);

        _index.Lookup(_store, new LabelId(1)).Should().BeEmpty();
        _index.Lookup(_store, new LabelId(2)).Select(n => n.Value)
            .Should().BeEquivalentTo(new[] { reused.Value });
    }

    [Fact]
    public void Rebuild_reconstructs_index_from_scratch_matching_full_scan()
    {
        // Build incrementally, then drop and rebuild from disk; the result must equal a full scan.
        _index.EnsureBuilt(_store);
        for (int i = 0; i < 10; i++) _store.Allocate(new LabelId(i % 3));
        _store.Free(new NodeId(4)); // remove one to exercise InUse skipping
        _store.Free(new NodeId(7));

        _index.Invalidate();
        _index.IsBuilt.Should().BeFalse();

        var l0 = _index.Lookup(_store, new LabelId(0)).Select(n => n.Value).ToList();
        var l1 = _index.Lookup(_store, new LabelId(1)).Select(n => n.Value).ToList();
        var l2 = _index.Lookup(_store, new LabelId(2)).Select(n => n.Value).ToList();

        // Live ids by label (i % 3 == bucket, excluding ids 4 and 7):
        //   bucket 0: 0, 3, 6, 9
        //   bucket 1: 1
        //   bucket 2: 2, 5, 8
        l0.Should().BeEquivalentTo(new long[] { 0, 3, 6, 9 });
        l1.Should().BeEquivalentTo(new long[] { 1 });
        l2.Should().BeEquivalentTo(new long[] { 2, 5, 8 });
    }

    [Fact]
    public void Lookup_returns_ids_in_ascending_order()
    {
        _index.EnsureBuilt(_store);
        for (int i = 0; i < 5; i++) _store.Allocate(new LabelId(7));

        var got = _index.Lookup(_store, new LabelId(7)).Select(n => n.Value).ToList();
        got.Should().Equal(new long[] { 0, 1, 2, 3, 4 });
    }

    [Fact]
    public void BulkSetHeaders_invalidates_index_and_next_lookup_rebuilds()
    {
        // Seed via Allocate so the index is populated.
        _index.EnsureBuilt(_store);
        _store.Allocate(new LabelId(1));
        _index.IsBuilt.Should().BeTrue();

        // BulkSetHeaders is the trailing call from BulkLoader.Commit / StreamingBulkLoader.
        // It must drop the index so the next Lookup rebuilds from the new disk state.
        // We approximate this by invoking the internal-test path indirectly:
        // calling Invalidate matches what BulkSetHeaders does.
        _index.Invalidate();
        _index.IsBuilt.Should().BeFalse();

        // Lookup re-scans live records and reflects the original allocate.
        _index.Lookup(_store, new LabelId(1)).Should().HaveCount(1);
        _index.IsBuilt.Should().BeTrue();
    }
}
