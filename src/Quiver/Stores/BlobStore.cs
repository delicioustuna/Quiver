using System.Buffers.Binary;
using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

// Blob page body (8160 bytes):
//  0 NextPageId(8) | 8 TotalLen(8, first page only) | 16 Data(8144)
internal sealed class BlobStore
{
    private const int DataOffset = 16;
    private const int DataPerPage = RecordPageMapping.PageBodySize - DataOffset; // 8144

    private static readonly PageId HeaderPageId = new(1);
    private const int MetaFreeHead = 0; // int64 (free blob page list head)

    private readonly IPagedFile _file;
    private long _blobFreeHead;

    public BlobStore(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= 1)
        {
            _file.AllocatePage(PageKind.Header);
            _blobFreeHead = -1;
            FlushMeta();
        }
        else
        {
            LoadMeta();
        }
    }

    public long Write(ReadOnlySpan<byte> data)
    {
        long totalLen = data.Length;
        long firstPageId = -1;
        long prevPageId = -1;

        int written = 0;
        while (written < totalLen || (written == 0 && totalLen == 0))
        {
            long pid = AllocBlobPage();
            if (firstPageId < 0) firstPageId = pid;

            // Link previous page to this one
            if (prevPageId >= 0)
            {
                var prev = _file.PinForWrite(new PageId(prevPageId));
                BinaryPrimitives.WriteInt64LittleEndian(prev.Data, pid);
                _file.UnpinDirty(new PageId(prevPageId), 0);
            }

            int toCopy = Math.Min(DataPerPage, (int)(totalLen - written));
            var ph = _file.PinForWrite(new PageId(pid));
            Span<byte> body = ph.Data;
            BinaryPrimitives.WriteInt64LittleEndian(body, -1L);          // NextPageId
            BinaryPrimitives.WriteInt64LittleEndian(body[8..], totalLen); // TotalLen
            data.Slice(written, toCopy).CopyTo(body[DataOffset..]);
            _file.UnpinDirty(new PageId(pid), 0);

            written += toCopy;
            prevPageId = pid;

            if (totalLen == 0) break; // single empty blob page
        }

        return firstPageId;
    }

    public long GetLength(long blobId)
    {
        using var h = _file.PinForRead(new PageId(blobId));
        return BinaryPrimitives.ReadInt64LittleEndian(h.Data[8..]);
    }

    public int Read(long blobId, Span<byte> destination)
    {
        long pid = blobId;
        int totalRead = 0;
        bool first = true;
        while (pid >= 0 && totalRead < destination.Length)
        {
            using var h = _file.PinForRead(new PageId(pid));
            long nextPid = BinaryPrimitives.ReadInt64LittleEndian(h.Data);
            long totalLen = first ? BinaryPrimitives.ReadInt64LittleEndian(h.Data[8..]) : 0;
            first = false;

            ReadOnlySpan<byte> data = h.Data[DataOffset..];
            int remaining = destination.Length - totalRead;
            int toCopy = Math.Min(data.Length, remaining);
            // Don't read past total length
            if (totalLen > 0)
                toCopy = (int)Math.Min(toCopy, totalLen - totalRead);
            data[..toCopy].CopyTo(destination[totalRead..]);
            totalRead += toCopy;
            pid = nextPid;
        }
        return totalRead;
    }

    public void Free(long blobId)
    {
        long pid = blobId;
        while (pid >= 0)
        {
            using var h = _file.PinForRead(new PageId(pid));
            long nextPid = BinaryPrimitives.ReadInt64LittleEndian(h.Data);

            var ph = _file.PinForWrite(new PageId(pid));
            ph.Data.Clear();
            BinaryPrimitives.WriteInt64LittleEndian(ph.Data, _blobFreeHead);
            _file.UnpinDirty(new PageId(pid), 0);
            _blobFreeHead = pid;

            pid = nextPid;
        }
        FlushMeta();
    }

    private long AllocBlobPage()
    {
        if (_blobFreeHead >= 0)
        {
            long pid = _blobFreeHead;
            using var h = _file.PinForRead(new PageId(pid));
            _blobFreeHead = BinaryPrimitives.ReadInt64LittleEndian(h.Data);
            FlushMeta();
            return pid;
        }
        var newId = _file.AllocatePage(PageKind.BTreeLeaf); // reuse kind for blob pages
        return newId.Value;
    }

    /// <summary>
    /// FT-15: ヘッダページからインメモリのメタ (blobFreeHead) を読み直す。
    /// abort の before-image 巻き戻し後、およびクラッシュ recovery 後に呼ばれる。
    /// </summary>
    internal void ReloadMeta() => LoadMeta();

    private void LoadMeta()
    {
        using var h = _file.PinForRead(HeaderPageId);
        _blobFreeHead = BinaryPrimitives.ReadInt64LittleEndian(h.Data[MetaFreeHead..]);
    }

    private void FlushMeta()
    {
        var ph = _file.PinForWrite(HeaderPageId);
        BinaryPrimitives.WriteInt64LittleEndian(ph.Data[MetaFreeHead..], _blobFreeHead);
        _file.UnpinDirty(HeaderPageId, 0);
    }
}
