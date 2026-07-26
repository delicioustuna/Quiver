using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Quiver.Storage.Records;
using Xunit;

namespace Quiver.Storage.Records.Tests;

public sealed class VectorPayloadStoreTests : IDisposable
{
    private readonly string _metadataPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly string _blobPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly PagedFile _metadata;
    private readonly PagedFile _blob;
    private readonly VectorPayloadStore _store;

    public VectorPayloadStoreTests()
    {
        _metadata = new PagedFile(_metadataPath);
        _blob = new PagedFile(_blobPath);
        _store = new VectorPayloadStore(_metadata, _blob);
    }

    [Fact]
    public void Read_validates_dimensions_length_checksum_and_generation()
    {
        VectorPayloadRef reference = _store.Write([1.0f, 2.0f, 3.0f]);

        _store.Read(reference).Should().Equal(1.0f, 2.0f, 3.0f);
        Action staleRead = () => _store.Read(reference with { Generation = 2 });
        staleRead.Should().Throw<CorruptionException>();

        using (var page = _metadata.PinForWrite(new PageId(2)))
            page.Data[16] ^= 0xFF;
        Action corruptRead = () => _store.Read(reference);
        corruptRead.Should().Throw<CorruptionException>();
    }

    [Fact]
    public void ScanOrphans_returns_only_unreachable_payload_references()
    {
        VectorPayloadRef reachable = _store.Write([1.0f]);
        VectorPayloadRef orphan = _store.Write([2.0f]);

        _store.ScanOrphans(new HashSet<VectorPayloadRef> { reachable })
            .Should().Equal(orphan);
    }

    public void Dispose()
    {
        _metadata.Dispose();
        _blob.Dispose();
        File.Delete(_metadataPath);
        File.Delete(_blobPath);
    }
}
