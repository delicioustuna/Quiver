using FluentAssertions;
using Quiver.Index;
using Quiver.Storage;
using Xunit;

namespace Quiver.Index.Tests;

public class BTreeIndexTests : IDisposable
{
    private readonly string _dir;
    // 索引は graph.quiver コンテナ上のテナント。各テストが作る standalone
    // IndexManager を追跡し、テスト終了時にまとめて Dispose してから dir を消す
    // (Dispose しないと container の MMF がロックされ Directory.Delete が失敗する)。
    private readonly List<IndexManager> _managers = new();

    public BTreeIndexTests()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var m in _managers) m.Dispose();
        System.IO.Directory.Delete(_dir, recursive: true);
    }

    private IndexManager NewMgr()
    {
        var m = IndexManager.OpenStandalone(_dir);
        _managers.Add(m);
        return m;
    }

    private IBTreeIndex<int> OpenInt32() => NewMgr().CreateInt32Index("test");

    [Fact]
    public void Insert_and_seek_single_entry()
    {
        using var idx = OpenInt32();
        idx.Insert(42, 100L);
        idx.EntryCount.Should().Be(1);
        var en = idx.Seek(42);
        en.MoveNext().Should().BeTrue();
        en.Current.Should().Be(100L);
        en.MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Insert_multiple_and_seek()
    {
        using var idx = OpenInt32();
        idx.Insert(10, 1); idx.Insert(20, 2); idx.Insert(30, 3);
        var en = idx.Seek(20);
        en.MoveNext().Should().BeTrue();
        en.Current.Should().Be(2);
    }

    [Fact]
    public void Delete_removes_entry()
    {
        using var idx = OpenInt32();
        idx.Insert(5, 50);
        idx.Delete(5, 50).Should().BeTrue();
        idx.EntryCount.Should().Be(0);
        idx.Seek(5).MoveNext().Should().BeFalse();
    }

    [Fact]
    public void Delete_nonexistent_returns_false()
    {
        using var idx = OpenInt32();
        idx.Delete(99, 0).Should().BeFalse();
    }

    [Fact]
    public void Range_scan_returns_in_order()
    {
        using var idx = OpenInt32();
        for (int i = 1; i <= 10; i++) idx.Insert(i, i * 10L);

        var values = new List<long>();
        var en = idx.Range(3, true, 7, true);
        while (en.MoveNext()) values.Add(en.Current.Value);
        values.Should().Equal(30L, 40L, 50L, 60L, 70L);
    }

    [Fact]
    public void Range_exclusive_bounds()
    {
        using var idx = OpenInt32();
        for (int i = 1; i <= 5; i++) idx.Insert(i, i);
        var values = new List<long>();
        var en = idx.Range(1, false, 5, false);
        while (en.MoveNext()) values.Add(en.Current.Value);
        values.Should().Equal(2L, 3L, 4L);
    }

    [Fact]
    public void FullScan_returns_all_in_order()
    {
        using var idx = OpenInt32();
        int[] input = [5, 3, 8, 1, 9, 2, 7, 4, 6, 10];
        foreach (int v in input) idx.Insert(v, v);
        var values = new List<long>();
        var en = idx.FullScan();
        while (en.MoveNext()) values.Add(en.Current.Value);
        values.Should().BeInAscendingOrder();
        values.Should().HaveCount(10);
    }

    [Fact]
    public void Large_insert_causes_splits()
    {
        using var idx = OpenInt32();
        for (int i = 0; i < 2000; i++) idx.Insert(i, i);
        idx.EntryCount.Should().Be(2000);
        idx.Height.Should().BeGreaterThan(1);
        var en = idx.Seek(1000);
        en.MoveNext().Should().BeTrue();
        en.Current.Should().Be(1000L);
    }

    [Fact]
    public void String_index_round_trip()
    {
        using var idx = NewMgr().CreateStringIndex("strtest");
        idx.Insert("banana", 2);
        idx.Insert("apple", 1);
        idx.Insert("cherry", 3);
        var values = new List<long>();
        var en = idx.FullScan();
        while (en.MoveNext()) values.Add(en.Current.Value);
        values.Should().Equal(1L, 2L, 3L); // alphabetical order
    }

    // -----------------------------------------------------------------------
    // 使用率の低いページの merge / redistribute
    // -----------------------------------------------------------------------

    [Fact]
    public void Mass_delete_collapses_height_back_to_one()
    {
        using var idx = OpenInt32();
        for (int i = 0; i < 4000; i++) idx.Insert(i, i);
        idx.Height.Should().BeGreaterThan(1);

        // 1 件残して全削除 → merge が伝搬し木は単一リーフへ縮退するはず。
        for (int i = 0; i < 3999; i++) idx.Delete(i, i).Should().BeTrue();

        idx.EntryCount.Should().Be(1);
        idx.Height.Should().Be(1);
        var en = idx.Seek(3999);
        en.MoveNext().Should().BeTrue();
        en.Current.Should().Be(3999L);
    }

    [Fact]
    public void Mass_delete_reclaims_pages_to_free_list()
    {
        var mgr = NewMgr();
        using var idx = mgr.CreateInt32Index("reclaim");
        for (int i = 0; i < 4000; i++) idx.Insert(i, i);
        long peak = mgr.GetIndexTenantPageCount("reclaim");

        // ほぼ全削除で merge が走り、空いたページは free list へ戻る。
        for (int i = 0; i < 3999; i++) idx.Delete(i, i);
        long afterDelete = mgr.GetIndexTenantPageCount("reclaim");

        // 再 insert は free list のページを再利用するため、ファイル末尾は
        // ほとんど伸びない (回収が効いていれば peak を大きく超えない)。
        for (int i = 10000; i < 13000; i++) idx.Insert(i, i);
        long afterReinsert = mgr.GetIndexTenantPageCount("reclaim");

        // 回収が効いていれば再 insert は free list を食うので、ファイルは数ページしか
        // 伸びない。回収が無ければ peak と同程度 (約 17 ページ) 伸びるはずなので、
        // peak + 5 を上限にして「再利用が起きている」ことを判定する。
        afterReinsert.Should().BeLessThan(peak + 5,
            "freed pages should be reused instead of extending the file");
        // free list 投入では PageCount 自体は縮まない (物理 truncate は別処理が担う)。
        afterDelete.Should().Be(peak);
    }

    [Fact]
    public void Delete_with_merge_preserves_remaining_entries()
    {
        using var idx = OpenInt32();
        for (int i = 0; i < 3000; i++) idx.Insert(i, i * 2L);

        // 偶数キーを全削除 → 多数のページが under-fill して merge/borrow が起きる。
        for (int i = 0; i < 3000; i += 2) idx.Delete(i, i * 2L).Should().BeTrue();

        idx.EntryCount.Should().Be(1500);
        // 残った奇数キーがすべて正しく引けること。
        for (int i = 1; i < 3000; i += 2)
        {
            var en = idx.Seek(i);
            en.MoveNext().Should().BeTrue($"key {i} should survive");
            en.Current.Should().Be(i * 2L);
        }
        // FullScan が昇順かつ件数一致。
        var scanned = new List<long>();
        var fs = idx.FullScan();
        while (fs.MoveNext()) scanned.Add(fs.Current.Value);
        scanned.Should().HaveCount(1500).And.BeInAscendingOrder();
    }

    [Fact]
    public void Delete_merge_survives_reopen()
    {
        var mgr = NewMgr();
        var idx = mgr.CreateInt32Index("persist");
        for (int i = 0; i < 4000; i++) idx.Insert(i, i);
        for (int i = 0; i < 3990; i++) idx.Delete(i, i);
        idx.Flush();
        mgr.Dispose();

        // 再 open して残り 10 件が正しく読めること (merge 後の構造が永続化されている)。
        var mgr2 = NewMgr();
        using var idx2 = mgr2.CreateInt32Index("persist");
        idx2.EntryCount.Should().Be(10);
        for (int i = 3990; i < 4000; i++)
        {
            var en = idx2.Seek(i);
            en.MoveNext().Should().BeTrue();
            en.Current.Should().Be(i);
        }
        mgr2.Dispose();
    }

    [Fact]
    public void String_index_delete_merge_round_trip()
    {
        var mgr = NewMgr();
        using var idx = mgr.CreateStringIndex("strmerge");
        for (int i = 0; i < 2000; i++) idx.Insert($"key{i:D6}", i);
        for (int i = 0; i < 1990; i++) idx.Delete($"key{i:D6}", i).Should().BeTrue();

        idx.EntryCount.Should().Be(10);
        for (int i = 1990; i < 2000; i++)
        {
            var en = idx.Seek($"key{i:D6}");
            en.MoveNext().Should().BeTrue($"key{i:D6} should survive merges");
            en.Current.Should().Be(i);
        }
    }
}
