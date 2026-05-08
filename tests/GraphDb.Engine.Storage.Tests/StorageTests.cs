using Xunit;
using GraphDb.Engine.Storage;
using GraphDb.Engine.Core;
using FluentAssertions;

namespace GraphDb.Engine.Storage.Tests;

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
    public void BodySize_Is8160()
    {
        PagedFile.BodySize.Should().Be(8160);
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

    // ------------------------------------------------------------------
    // AllocatePage
    // ------------------------------------------------------------------

    [Fact]
    public void AllocatePage_Returns_SequentialIds()
    {
        using var f = new PagedFile(TmpFile());

        var p1 = f.AllocatePage(PageKind.NodeRecord);
        var p2 = f.AllocatePage(PageKind.NodeRecord);

        p1.Value.Should().Be(1);
        p2.Value.Should().Be(2);
        f.PageCount.Should().Be(3);
    }

    [Fact]
    public void AllocatePage_PageIsReadable_AfterWrite()
    {
        using var f = new PagedFile(TmpFile());
        var pageId = f.AllocatePage(PageKind.NodeRecord);

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
            var pageId = f.AllocatePage(PageKind.NodeRecord);

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
    // Free List
    // ------------------------------------------------------------------

    [Fact]
    public void FreePage_ThenAllocate_ReusesSameId()
    {
        using var f = new PagedFile(TmpFile());

        var p1 = f.AllocatePage(PageKind.NodeRecord);
        var p2 = f.AllocatePage(PageKind.NodeRecord);

        f.FreePage(p1); // p1 を Free List へ

        var p3 = f.AllocatePage(PageKind.NodeRecord); // p1 が再利用されるはず
        p3.Should().Be(p1);
    }

    [Fact]
    public void FreeList_MultiplePages_WorksAsStack()
    {
        using var f = new PagedFile(TmpFile());

        var p1 = f.AllocatePage(PageKind.NodeRecord);
        var p2 = f.AllocatePage(PageKind.NodeRecord);
        var p3 = f.AllocatePage(PageKind.NodeRecord);

        f.FreePage(p1);
        f.FreePage(p2); // スタック: p2 → p1

        // LIFO 順で再利用
        var r1 = f.AllocatePage(PageKind.NodeRecord);
        var r2 = f.AllocatePage(PageKind.NodeRecord);
        r1.Should().Be(p2);
        r2.Should().Be(p1);
    }

    [Fact]
    public void FreeList_DoesNotGrowPageCount()
    {
        using var f = new PagedFile(TmpFile());
        var p1 = f.AllocatePage(PageKind.NodeRecord);
        long countBefore = f.PageCount;

        f.FreePage(p1);
        var p2 = f.AllocatePage(PageKind.NodeRecord);

        f.PageCount.Should().Be(countBefore); // 再利用なので増えない
    }

    // ------------------------------------------------------------------
    // ヘッダ検証
    // ------------------------------------------------------------------

    [Fact]
    public void CorruptMagic_ThrowsCorruptionException()
    {
        string path = TmpFile();
        PageId pageId;

        using (var f = new PagedFile(path))
            pageId = f.AllocatePage(PageKind.NodeRecord);

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
        catch (CorruptionException)
        {
            threw = true;
        }
        threw.Should().BeTrue("corrupt magic should raise CorruptionException");
    }

    // ------------------------------------------------------------------
    // IPageManager
    // ------------------------------------------------------------------

    [Fact]
    public void PageManager_OpenOrCreate_ReturnsPagedFile()
    {
        using var mgr = new PageManager();
        using var pf = mgr.OpenOrCreate(TmpFile(), PageKind.NodeRecord);

        pf.PageSize.Should().Be(8192);
    }
}
