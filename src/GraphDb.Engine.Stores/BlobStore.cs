using GraphDb.Engine.Storage;

namespace GraphDb.Engine.Stores;

internal sealed class BlobStore
{
    private readonly IPagedFile _file;

    public BlobStore(IPagedFile file)
    {
        _file = file;
    }

    public long Write(ReadOnlySpan<byte> data) => throw new NotImplementedException();
    public int Read(long blobId, Span<byte> destination) => throw new NotImplementedException();
    public void Free(long blobId) => throw new NotImplementedException();
}
