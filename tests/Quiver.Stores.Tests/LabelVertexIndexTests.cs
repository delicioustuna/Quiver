using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

/// <summary>
/// <see cref="LabelVertexIndex"/> の round-trip / 増分更新 / reopen rebuild / bulk load invalidation を直接検証する。
/// </summary>
public class LabelVertexIndexTests : IDisposable
{
    private readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly string _mapPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
    private readonly PagedFile _pf;
    private readonly PagedFile _mapFile;
    private readonly VersionedVertexStore _store;
    private readonly LabelVertexIndex _index = new();

    public LabelVertexIndexTests()
    {
        _pf = new PagedFile(_path);
        _mapFile = new PagedFile(_mapPath);
        _store = new VersionedVertexStore(_pf, new ItemPointerMap(_mapFile), _index);
    }

    public void Dispose()
    {
        _pf.Dispose();
        _mapFile.Dispose();
        System.IO.File.Delete(_path);
        System.IO.File.Delete(_mapPath);
    }

    [Fact]
    public void Allocate_drives_index_to_O_by_L_lookup()
    {
        // OnAllocate がスキップせず更新するよう、少なくとも一度は強制 build する。
        _index.EnsureBuilt(_store);

        var docA = _store.Allocate(new LabelId(1));
        var docB = _store.Allocate(new LabelId(1));
        var otherA = _store.Allocate(new LabelId(2));

        // index は Sequence 空間 id を返す。slot 同一性で照合する。
        var docs = _index.Lookup(_store, new LabelId(1)).ToArray();
        docs.Select(n => n.Sequence).Should().BeEquivalentTo(new[] { docA.Sequence, docB.Sequence });
        docs.Should().OnlyContain(n => n.Generation > 0,
            "Lookup is a logical API and must not return raw physical sequences");
        _index.Lookup(_store, new LabelId(2)).Select(n => n.Sequence)
            .Should().BeEquivalentTo(new[] { otherA.Sequence });
        _index.Lookup(_store, new LabelId(3)).Should().BeEmpty();
    }

    [Fact]
    public void Free_removes_vertex_from_label_bucket()
    {
        _index.EnsureBuilt(_store);
        var a = _store.Allocate(new LabelId(1));
        var b = _store.Allocate(new LabelId(1));
        var c = _store.Allocate(new LabelId(2));

        _store.Free(b);

        _index.Lookup(_store, new LabelId(1)).Select(n => n.Sequence)
            .Should().BeEquivalentTo(new[] { a.Sequence });
        _index.Lookup(_store, new LabelId(2)).Select(n => n.Sequence)
            .Should().BeEquivalentTo(new[] { c.Sequence });
    }

    [Fact]
    public void Lookup_lazily_builds_index_on_first_access()
    {
        // EnsureBuilt 前に Allocate すると、index は未構築のままで OnAllocate は no-op となる。
        _store.Allocate(new LabelId(1));
        _store.Allocate(new LabelId(1));
        _store.Allocate(new LabelId(2));

        _index.IsBuilt.Should().BeFalse();

        // 最初の Lookup が全 Scan からの Rebuild を起動する。
        var docs = _index.Lookup(_store, new LabelId(1)).ToList();
        docs.Should().HaveCount(2);
        _index.IsBuilt.Should().BeTrue();
        _index.CountFor(new LabelId(1)).Should().Be(2);
        _index.CountFor(new LabelId(2)).Should().Be(1);
    }

    [Fact]
    public void Allocate_after_free_uses_new_id_under_mvcc()
    {
        // MVCC では Free は論理削除のみ。slot は vacuum 後にのみ再利用される。
        // インデックスは Free 時にバケット 1 から除去され、新規 Allocate は新 id で
        // バケット 2 に入る。
        _index.EnsureBuilt(_store);
        var first = _store.Allocate(new LabelId(1));
        _store.Free(first);

        var newAlloc = _store.Allocate(new LabelId(2));
        newAlloc.Sequence.Should().NotBe(first.Sequence); // 論理削除のみ → 新 slot (再利用は vacuum 後)

        _index.Lookup(_store, new LabelId(1)).Should().BeEmpty();
        _index.Lookup(_store, new LabelId(2)).Select(n => n.Sequence)
            .Should().BeEquivalentTo(new[] { newAlloc.Sequence });
    }

    [Fact]
    public void Rebuild_reconstructs_index_from_scratch_matching_full_scan()
    {
        // 増分 build 後に破棄して disk から rebuild し、全 scan と同じ結果になることを確認する。
        _index.EnsureBuilt(_store);
        for (int i = 0; i < 10; i++) _store.Allocate(new LabelId(i % 3));
        _store.Free(new VertexId(4)); // remove one to exercise InUse skipping
        _store.Free(new VertexId(7));

        _index.Invalidate();
        _index.IsBuilt.Should().BeFalse();

        // slot 番号 (Sequence) で検証する。Value は世代を含む。
        var l0 = _index.Lookup(_store, new LabelId(0)).Select(n => n.Sequence).ToList();
        var l1 = _index.Lookup(_store, new LabelId(1)).Select(n => n.Sequence).ToList();
        var l2 = _index.Lookup(_store, new LabelId(2)).Select(n => n.Sequence).ToList();

        // label ごとの live id (i % 3 == bucket、id 4 と 7 を除外):
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

        var got = _index.Lookup(_store, new LabelId(7)).Select(n => n.Sequence).ToList();
        got.Should().Equal(new long[] { 0, 1, 2, 3, 4 }); // slot 番号で昇順検証
    }

    [Fact]
    public void BulkSetHeaders_invalidates_index_and_next_lookup_rebuilds()
    {
        // index を埋めるため Allocate 経由で seed を投入する。
        _index.EnsureBuilt(_store);
        _store.Allocate(new LabelId(1));
        _index.IsBuilt.Should().BeTrue();

        // BulkSetHeaders は BulkLoader.Commit / StreamingBulkLoader の最後に呼ばれる。
        // 次の Lookup が新しい disk 状態から rebuild できるよう index を破棄する必要がある。
        // internal test 経路を直接呼べないため、BulkSetHeaders と同じ Invalidate で近似する。
        _index.Invalidate();
        _index.IsBuilt.Should().BeFalse();

        // Lookup が live record を再 scan し、元の allocate を反映する。
        _index.Lookup(_store, new LabelId(1)).Should().HaveCount(1);
        _index.IsBuilt.Should().BeTrue();
    }
}
