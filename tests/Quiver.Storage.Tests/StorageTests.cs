using Xunit;
using Quiver.Storage;
using Quiver.Core;
using FluentAssertions;

namespace Quiver.Storage.Tests;

public class StorageTests : IDisposable
{
    private readonly string _tmpDir;

    public StorageTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() => Directory.Delete(_tmpDir, recursive: true);

    private string TmpFile(string name = "test.db") => Path.Combine(_tmpDir, name);

    // ------------------------------------------------------------------
    // 基本仕様
    // ------------------------------------------------------------------

    [Fact]
    public void PageSize_Is8192()
    {
        PagedFile.PageSizeConst.Should().Be(8192);
    }

    [Fact]
    public void BodySize_Is8152()
    {
        PagedFile.BodySize.Should().Be(8152);
    }

    // ------------------------------------------------------------------
    // 新規ファイル作成
    // ------------------------------------------------------------------

    [Fact]
    public void NewFile_PageCount_Is1()
    {
        using var f = new PagedFile(TmpFile());
        f.PageCount.Should().Be(1); // メタページのみ
    }

    [Fact]
    public void NewFile_starts_at_one_mib_and_doubles_without_losing_data()
    {
        string path = TmpFile();
        PageId markerPage;

        using (var file = new PagedFile(path))
        {
            new FileInfo(path).Length.Should().Be(1L * 1024 * 1024);
            markerPage = file.AllocatePage(PageKind.VertexRecord);
            using (var page = file.PinForWrite(markerPage))
                page.Data[0] = 0xA5;

            while (new FileInfo(path).Length == 1L * 1024 * 1024)
                file.AllocatePage(PageKind.VertexRecord);

            new FileInfo(path).Length.Should().Be(2L * 1024 * 1024);
        }

        using (var reopened = new PagedFile(path))
        {
            using (var page = reopened.PinForRead(markerPage))
                page.Data[0].Should().Be(0xA5);

            while (new FileInfo(path).Length == 2L * 1024 * 1024)
                reopened.AllocatePage(PageKind.VertexRecord);

            new FileInfo(path).Length.Should().Be(4L * 1024 * 1024);
            (new FileInfo(path).Length % PagedFile.PageSizeConst).Should().Be(0);
            using var pageAfterGrowth = reopened.PinForRead(markerPage);
            pageAfterGrowth.Data[0].Should().Be(0xA5);
        }
    }

    [Fact]
    public void Configured_growth_step_is_aligned_and_capped()
    {
        string path = TmpFile();
        using var file = new PagedFile(
            path,
            initialFileAllocationBytes: 2L * PagedFile.PageSizeConst,
            maximumFileGrowthStepBytes: 4L * PagedFile.PageSizeConst);

        var observedLengths = new List<long> { new FileInfo(path).Length };
        for (int i = 0; i < 12; i++)
        {
            file.AllocatePage(PageKind.VertexRecord);
            long length = new FileInfo(path).Length;
            if (length != observedLengths[^1]) observedLengths.Add(length);
        }

        observedLengths.Should().Equal(
            2L * PagedFile.PageSizeConst,
            4L * PagedFile.PageSizeConst,
            8L * PagedFile.PageSizeConst,
            12L * PagedFile.PageSizeConst,
            16L * PagedFile.PageSizeConst);
        observedLengths.Should().OnlyContain(length => length % PagedFile.PageSizeConst == 0);
    }

    // ------------------------------------------------------------------
    // ページ割り当て
    // ------------------------------------------------------------------

    [Fact]
    public void AllocatePage_Returns_SequentialIds()
    {
        using var f = new PagedFile(TmpFile());

        var p1 = f.AllocatePage(PageKind.VertexRecord);
        var p2 = f.AllocatePage(PageKind.VertexRecord);

        p1.Value.Should().Be(1);
        p2.Value.Should().Be(2);
        f.PageCount.Should().Be(3);
    }

    [Fact]
    public void AllocatePage_PageIsReadable_AfterWrite()
    {
        using var f = new PagedFile(TmpFile());
        var pageId = f.AllocatePage(PageKind.VertexRecord);

        // 書き込み
        using (var wh = f.PinForWrite(pageId))
        {
            wh.Data[0] = 0xAB;
            wh.Data[1] = 0xCD;
        }
        f.Flush();

        // 再読み取り
        using var rh = f.PinForRead(pageId);
        rh.Data[0].Should().Be(0xAB);
        rh.Data[1].Should().Be(0xCD);
    }

    // ------------------------------------------------------------------
    // Flush & 再オープン
    // ------------------------------------------------------------------

    [Fact]
    public void WriteFlushReopen_DataPersists()
    {
        string path = TmpFile();

        using (var f = new PagedFile(path))
        {
            var pageId = f.AllocatePage(PageKind.VertexRecord);

            using var wh = f.PinForWrite(pageId);
            wh.Data[0] = 0x42;
            wh.Data[100] = 0xFF;
        } // Dispose が Flush を呼ぶ

        using (var f2 = new PagedFile(path))
        {
            f2.PageCount.Should().Be(2);
            var pageId = new PageId(1);
            using var rh = f2.PinForRead(pageId);
            rh.Data[0].Should().Be(0x42);
            rh.Data[100].Should().Be(0xFF);
        }
    }

    // ------------------------------------------------------------------
    // 空きページリスト
    // ------------------------------------------------------------------

    [Fact]
    public void FreePage_ThenAllocate_ReusesSameId()
    {
        using var f = new PagedFile(TmpFile());

        var p1 = f.AllocatePage(PageKind.VertexRecord);
        var p2 = f.AllocatePage(PageKind.VertexRecord);

        f.FreePage(p1); // p1 を Free List へ

        var p3 = f.AllocatePage(PageKind.VertexRecord); // p1 が再利用されるはず
        p3.Should().Be(p1);
    }

    [Fact]
    public void FreeList_MultiplePages_WorksAsStack()
    {
        using var f = new PagedFile(TmpFile());

        var p1 = f.AllocatePage(PageKind.VertexRecord);
        var p2 = f.AllocatePage(PageKind.VertexRecord);
        var p3 = f.AllocatePage(PageKind.VertexRecord);

        f.FreePage(p1);
        f.FreePage(p2); // スタック: p2 → p1

        // LIFO 順で再利用
        var r1 = f.AllocatePage(PageKind.VertexRecord);
        var r2 = f.AllocatePage(PageKind.VertexRecord);
        r1.Should().Be(p2);
        r2.Should().Be(p1);
    }

    [Fact]
    public void FreeList_DoesNotGrowPageCount()
    {
        using var f = new PagedFile(TmpFile());
        var p1 = f.AllocatePage(PageKind.VertexRecord);
        long countBefore = f.PageCount;

        f.FreePage(p1);
        var p2 = f.AllocatePage(PageKind.VertexRecord);

        f.PageCount.Should().Be(countBefore); // 再利用なので増えない
    }

    // ------------------------------------------------------------------
    // ヘッダ検証
    // ------------------------------------------------------------------

    [Fact]
    public void CorruptMagic_ThrowsStorageFormatMismatchException()
    {
        string path = TmpFile();
        PageId pageId;

        using (var f = new PagedFile(path))
            pageId = f.AllocatePage(PageKind.VertexRecord);

        // ファイルを直接破壊 (Magic 先頭バイトを 0x00 にする)
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(pageId.Value * PagedFile.PageSizeConst, SeekOrigin.Begin);
            fs.WriteByte(0x00);
        }

        // PageReadHandle は ref struct のためラムダ内では使えない → try-catch で検証
        using var f2 = new PagedFile(path);
        bool threw = false;
        try
        {
            using var handle = f2.PinForRead(pageId);
        }
        catch (StorageFormatMismatchException)
        {
            threw = true;
        }
        threw.Should().BeTrue("corrupt magic should reject the storage family");
    }

    // ------------------------------------------------------------------
    // IPageManager 契約
    // ------------------------------------------------------------------

    [Fact]
    public void PageManager_OpenOrCreate_ReturnsPagedFile()
    {
        using var mgr = new PageManager();
        using var pf = mgr.OpenOrCreate(TmpFile(), PageKind.VertexRecord);

        pf.PageSize.Should().Be(8192);
    }

    // ------------------------------------------------------------------
    // 切り詰め
    // ------------------------------------------------------------------

    [Fact]
    public void Truncate_ShrinksPageCount_AndPhysicalSize()
    {
        string path = TmpFile();
        using (var f = new PagedFile(path))
        {
            for (int i = 0; i < 10; i++)
                f.AllocatePage(PageKind.VertexRecord);
            f.PageCount.Should().Be(11); // meta + 10
            f.Flush();
        }
        long beforeSize = new FileInfo(path).Length;

        using (var f = new PagedFile(path))
        {
            f.Truncate(3); // meta + 2 record pages
            f.PageCount.Should().Be(3);
        }

        long afterSize = new FileInfo(path).Length;
        afterSize.Should().BeLessThan(beforeSize);
        afterSize.Should().Be(3L * PagedFile.PageSizeConst);

        // 再 open しても PageCount が縮減後の値を保つこと。
        using var reopen = new PagedFile(path);
        reopen.PageCount.Should().Be(3);
    }

    [Fact]
    public void Truncate_IsNoOp_WhenNewCount_NotLessThanCurrent()
    {
        using var f = new PagedFile(TmpFile());
        f.AllocatePage(PageKind.VertexRecord);
        f.AllocatePage(PageKind.VertexRecord);
        f.PageCount.Should().Be(3);

        f.Truncate(3); // 同じ
        f.PageCount.Should().Be(3);
        f.Truncate(100); // 拡張は無視
        f.PageCount.Should().Be(3);
    }

    [Fact]
    public void Truncate_Throws_WhenNewCount_Below1()
    {
        using var f = new PagedFile(TmpFile());
        Action act = () => f.Truncate(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Truncate_AllowsReallocation_AfterShrink()
    {
        using var f = new PagedFile(TmpFile());
        for (int i = 0; i < 5; i++)
            f.AllocatePage(PageKind.VertexRecord);
        f.PageCount.Should().Be(6);

        f.Truncate(2);
        f.PageCount.Should().Be(2);

        // 縮減後にも新規ページが確保できて読める。
        var newId = f.AllocatePage(PageKind.VertexRecord);
        newId.Value.Should().Be(2);
        using (var wh = f.PinForWrite(newId))
        {
            wh.Data[0] = 0x55;
        }
        f.Flush();
        using var rh = f.PinForRead(newId);
        rh.Data[0].Should().Be(0x55);
    }
}
