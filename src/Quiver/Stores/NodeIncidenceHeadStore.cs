using Quiver.Core;
using Quiver.Storage;

namespace Quiver.Storage.Records;

/// <summary>node ごとの先頭 incidence (node chain の head) を読み書きする契約</summary>
internal interface INodeIncidenceHeadStore
{
    /// <summary>node chain の先頭 incidence を返す。未登録の node は Invalid。</summary>
    IncidenceId Get(NodeId nodeId);

    /// <summary>node chain の先頭 incidence を差し替える。</summary>
    void Set(NodeId nodeId, IncidenceId incidenceId);
}

// head を node record に持たせず sidecar に分離しているのは、hyperedge を使わない
// ワークロードの node payload 読み取り帯域を増やさないため (実測比較で採用)。
/// <summary>
/// node sequence を添字にして先頭 incidence sequence を保持する 6 バイト固定長 sidecar。
/// 未初期化 slot は Int48 の -1 であり、node payload のレイアウトを変更しない。
/// </summary>
internal sealed class NodeIncidenceHeadStore : INodeIncidenceHeadStore
{
    private const int RecordSize = 6;
    private const int HeaderFormatOffset = 31;
    private static readonly PageId HeaderPageId = new(1);
    private static int RecordsPerPage => RecordPageMapping.PageBodySize / RecordSize;

    private readonly IPagedFile _file;

    public NodeIncidenceHeadStore(IPagedFile file)
    {
        _file = file;
        if (_file.PageCount <= HeaderPageId.Value)
        {
            _file.AllocatePage(PageKind.Header);
            using var header = _file.PinForWrite(HeaderPageId);
            header.Data[HeaderFormatOffset] = FormatVersion.Current;
        }
        else
        {
            using var header = _file.PinForRead(HeaderPageId);
            byte actual = header.Data[HeaderFormatOffset];
            if (actual != FormatVersion.Current)
                throw new FormatVersionMismatchException(
                    "node-incidence-head", actual, FormatVersion.Current);
        }
    }

    public IncidenceId Get(NodeId nodeId)
    {
        long sequence = nodeId.Sequence;
        if (sequence < 0)
            return IncidenceId.Invalid;

        var (pageId, offset) = Location(sequence);
        if (pageId.Value >= _file.PageCount)
            return IncidenceId.Invalid;

        using var page = _file.PinForRead(pageId);
        return new IncidenceId(RecordHelpers.ReadInt48(page.Data[offset..]));
    }

    public void Set(NodeId nodeId, IncidenceId incidenceId)
    {
        long sequence = nodeId.Sequence;
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(nodeId));

        var (pageId, offset) = Location(sequence);
        EnsurePage(pageId);
        using var page = _file.PinForWrite(pageId);
        RecordHelpers.WriteInt48(page.Data[offset..], incidenceId.Sequence);
    }

    private static (PageId PageId, int Offset) Location(long sequence)
        => (new PageId(sequence / RecordsPerPage + 2),
            (int)(sequence % RecordsPerPage) * RecordSize);

    private void EnsurePage(PageId pageId)
    {
        while (_file.PageCount <= pageId.Value)
        {
            PageId allocated = _file.AllocatePage(PageKind.NodeRecord);
            using var page = _file.PinForWrite(allocated);
            // 全 bit 1 = 各 slot が Int48 の -1 (Invalid)。未使用 slot を 0 (有効な
            // sequence 0) と誤読しないための初期化。
            page.Data.Fill(byte.MaxValue);
        }
    }
}
