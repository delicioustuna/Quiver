using System.Buffers.Binary;
using Xunit;
using Quiver.Storage;
using Quiver.Core;
using FluentAssertions;

namespace Quiver.Storage.Tests;

/// <summary>
/// 単一ファイルコンテナ + テナント変換シムの page レベル検証。
/// ストアを載せる前に、論理↔物理 page table・テナント分離・単一ファイル永続・論理 free 再利用・
/// page-table 連鎖 + reopen を確認する。
/// </summary>
public class SingleFileContainerTests : IDisposable
{
    private readonly string _tmpDir;

    public SingleFileContainerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private string DbFile() => Path.Combine(_tmpDir, "test.quiver");

    private static void WriteMarker(IPagedFile t, PageId p, long marker)
    {
        var wh = t.PinForWrite(p);
        try { BinaryPrimitives.WriteInt64LittleEndian(wh.Data, marker); }
        finally { wh.Dispose(); }
    }

    private static long ReadMarker(IPagedFile t, PageId p)
    {
        var rh = t.PinForRead(p);
        try { return BinaryPrimitives.ReadInt64LittleEndian(rh.Data); }
        finally { rh.Dispose(); }
    }

    // ------------------------------------------------------------------
    // 単一ファイル
    // ------------------------------------------------------------------

    [Fact]
    public void Container_ProducesSingleFile()
    {
        using (var c = new SingleFileContainer(DbFile()))
        {
            var t = c.OpenTenant(1, PageKind.VertexRecord);
            WriteMarker(t, t.AllocatePage(PageKind.VertexRecord), 42);
        }
        // 静止時はディレクトリ内に *.quiver 1 ファイルのみ (WAL なし)。
        Directory.GetFiles(_tmpDir).Should().ContainSingle()
            .Which.Should().EndWith("test.quiver");
    }

    // ------------------------------------------------------------------
    // 論理ページ ID は 1 から
    // ------------------------------------------------------------------

    [Fact]
    public void FreshTenant_PageCountIsOne_AndAllocStartsAtOne()
    {
        using var c = new SingleFileContainer(DbFile());
        var t = c.OpenTenant(7, PageKind.VertexRecord);
        t.PageCount.Should().Be(1); // 論理 page 0 予約のみ

        var p1 = t.AllocatePage(PageKind.VertexRecord);
        p1.Value.Should().Be(1);
        t.PageCount.Should().Be(2);

        var p2 = t.AllocatePage(PageKind.VertexRecord);
        p2.Value.Should().Be(2);
    }

    // ------------------------------------------------------------------
    // テナント分離
    // ------------------------------------------------------------------

    [Fact]
    public void Tenants_AreIsolated()
    {
        using var c = new SingleFileContainer(DbFile());
        var t1 = c.OpenTenant(1, PageKind.VertexRecord);
        var t2 = c.OpenTenant(2, PageKind.EdgeRecord);

        // 両テナントとも論理 page 1 を持つが、物理ページは別。
        var a = t1.AllocatePage(PageKind.VertexRecord);
        var b = t2.AllocatePage(PageKind.EdgeRecord);
        a.Value.Should().Be(1);
        b.Value.Should().Be(1);

        WriteMarker(t1, a, 1001);
        WriteMarker(t2, b, 2002);

        ReadMarker(t1, a).Should().Be(1001);
        ReadMarker(t2, b).Should().Be(2002);
    }

    [Fact]
    public void OpenTenant_ReturnsSameInstance()
    {
        using var c = new SingleFileContainer(DbFile());
        var first = c.OpenTenant(3, PageKind.VertexRecord);
        var second = c.OpenTenant(3, PageKind.VertexRecord);
        ReferenceEquals(first, second).Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // 永続化 (reopen)
    // ------------------------------------------------------------------

    [Fact]
    public void Data_PersistsAcrossReopen()
    {
        string path = DbFile();
        PageId p1, p2;
        using (var c = new SingleFileContainer(path))
        {
            var t1 = c.OpenTenant(1, PageKind.VertexRecord);
            var t2 = c.OpenTenant(2, PageKind.EdgeRecord);
            p1 = t1.AllocatePage(PageKind.VertexRecord);
            p2 = t2.AllocatePage(PageKind.EdgeRecord);
            WriteMarker(t1, p1, 111);
            WriteMarker(t2, p2, 222);
            c.Flush();
        }

        using (var c = new SingleFileContainer(path))
        {
            var t1 = c.OpenTenant(1, PageKind.VertexRecord);
            var t2 = c.OpenTenant(2, PageKind.EdgeRecord);
            t1.PageCount.Should().Be(2);
            t2.PageCount.Should().Be(2);
            ReadMarker(t1, p1).Should().Be(111);
            ReadMarker(t2, p2).Should().Be(222);
        }
    }

    // ------------------------------------------------------------------
    // 論理 free 再利用
    // ------------------------------------------------------------------

    [Fact]
    public void FreePage_ReusesLogicalId()
    {
        using var c = new SingleFileContainer(DbFile());
        var t = c.OpenTenant(1, PageKind.VertexRecord);
        var a = t.AllocatePage(PageKind.VertexRecord); // 1
        var b = t.AllocatePage(PageKind.VertexRecord); // 2
        var cc = t.AllocatePage(PageKind.VertexRecord); // 3
        a.Value.Should().Be(1);
        b.Value.Should().Be(2);
        cc.Value.Should().Be(3);

        t.FreePage(b);
        var reused = t.AllocatePage(PageKind.VertexRecord);
        reused.Value.Should().Be(2); // free list から論理 2 を再利用
    }

    // ------------------------------------------------------------------
    // page-table 連鎖 + reopen (EntriesPerPage 境界越え)
    // ------------------------------------------------------------------

    [Fact]
    public void Truncate_ReclaimsTenantPages_AndPersists()
    {
        string path = DbFile();
        using (var c = new SingleFileContainer(path))
        {
            var t = c.OpenTenant(1, PageKind.VertexRecord);
            for (int i = 0; i < 10; i++)
                WriteMarker(t, t.AllocatePage(PageKind.VertexRecord), 100 + i);
            t.PageCount.Should().Be(11); // logical 0 予約 + 10

            ((IPagedFile)t).Truncate(6); // 論理 1..5 を残し 6..10 を除去
            t.PageCount.Should().Be(6);

            // 残った論理ページは読める。
            ReadMarker(t, new PageId(1)).Should().Be(100);
            ReadMarker(t, new PageId(5)).Should().Be(104);

            // 解放された物理ページは再割当で再利用され、論理は 6 から再成長する。
            var reused = t.AllocatePage(PageKind.VertexRecord);
            reused.Value.Should().Be(6);
            WriteMarker(t, reused, 999);
            c.Flush();
        }
        using (var c = new SingleFileContainer(path))
        {
            var t = c.OpenTenant(1, PageKind.VertexRecord);
            t.PageCount.Should().Be(7);
            ReadMarker(t, new PageId(1)).Should().Be(100);
            ReadMarker(t, new PageId(6)).Should().Be(999);
        }
    }

    [Fact]
    public void PageTableChaining_AcrossManyPages_PersistsAcrossReopen()
    {
        string path = DbFile();
        int n = SingleFileContainer.EntriesPerPageTablePage + 50; // 連鎖を確実に跨ぐ
        long firstLogical, lastLogical;
        using (var c = new SingleFileContainer(path))
        {
            var t = c.OpenTenant(1, PageKind.VertexRecord);
            PageId first = t.AllocatePage(PageKind.VertexRecord);
            firstLogical = first.Value;
            WriteMarker(t, first, 7777);
            PageId last = first;
            for (int i = 1; i < n; i++)
                last = t.AllocatePage(PageKind.VertexRecord);
            lastLogical = last.Value;
            WriteMarker(t, last, 9999);
            c.Flush();
        }

        using (var c = new SingleFileContainer(path))
        {
            var t = c.OpenTenant(1, PageKind.VertexRecord);
            t.PageCount.Should().Be(n + 1); // 論理 page 0 予約 + n 割当
            ReadMarker(t, new PageId(firstLogical)).Should().Be(7777);
            ReadMarker(t, new PageId(lastLogical)).Should().Be(9999);
        }
    }
}
