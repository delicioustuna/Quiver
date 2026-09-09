using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Storage;
using Yatagarasu.Storage.Records;
using Yatagarasu.Storage.Wal;
using Yatagarasu.Transactions;
using Xunit;

namespace Yatagarasu.Storage.Records.Tests;

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

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(2040)]
    [InlineData(4096)]
    public void Free_reuses_blob_pages_without_reusing_metadata_identity(int dimensions)
    {
        float[] elements = Enumerable.Range(0, dimensions).Select(i => (float)i).ToArray();
        var original = _store.Write(elements);
        var survivor = _store.Write(new float[] { 42 });
        long pages = _blob.PageCount;
        Action stale = () => _store.Free(original with { Generation = 2 });
        stale.Should().Throw<CorruptionException>();
        _store.Read(original).Should().Equal(elements);

        _store.Free(original).Should().BeTrue();
        _store.Free(original).Should().BeFalse();
        stale.Should().Throw<CorruptionException>();
        Action absent = () => _store.Read(original);
        absent.Should().Throw<CorruptionException>();
        var replacement = _store.Write(elements);
        replacement.Sequence.Should().BeGreaterThan(survivor.Sequence);
        _blob.PageCount.Should().Be(pages);
        _store.Read(replacement).Should().Equal(elements);
        _store.Read(survivor).Should().Equal(42);
        _store.ScanOrphans(new HashSet<VectorPayloadRef> { survivor, replacement }).Should().BeEmpty();
    }

    [Fact]
    public void Free_rejects_corruption_before_modifying_blob_pages()
    {
        var reference = _store.Write(new float[] { 1, 2, 3 });
        byte[][] before = Enumerable.Range(0, (int)_blob.PageCount).Select(i =>
        {
            using var page = _blob.PinForRead(new PageId(i));
            return page.Raw.ToArray();
        }).ToArray();
        using (var page = _metadata.PinForWrite(new PageId(2))) page.Data[16] ^= 0xFF;
        Action free = () => _store.Free(reference);
        free.Should().Throw<CorruptionException>();
        for (int i = 0; i < before.Length; i++)
        {
            using var page = _blob.PinForRead(new PageId(i));
            page.Raw.ToArray().Should().Equal(before[i]);
        }
    }

    [Fact]
    public void Failed_metadata_tombstone_can_restore_blob_pages_and_retry()
    {
        float[] elements = Enumerable.Range(0, 4096).Select(i => (float)i).ToArray();
        var reference = _store.Write(elements);
        _metadata.Flush();
        _blob.Flush();
        using var wal = new NullWriteAheadLog();
        var writes = new WalWriteSet(wal, new TransactionId(1));
        wal.ActiveWriteSet = writes;
        _metadata.EnableWalLogging(1, wal);
        _blob.EnableWalLogging(2, wal);
        using (var held = _metadata.PinForRead(new PageId(2)))
        {
            Action free = () => _store.Free(reference);
            free.Should().Throw<LockRecursionException>();
        }
        var images = writes.GetAllBeforeImagesOldestWins();
        images.Count.Should().BeGreaterThan(1);
        wal.ActiveWriteSet = null;
        foreach (var image in images)
        {
            WalPageImageCodec.TryDecode(image, out byte kind, out long pageId, out byte[] bytes)
                .Should().BeTrue();
            (kind == 1 ? _metadata : _blob).WritePageForRecovery(new PageId(pageId), bytes);
        }
        _store.ReloadMeta();
        _store.Read(reference).Should().Equal(elements);
        _store.Free(reference).Should().BeTrue();
        _store.Free(reference).Should().BeFalse();
        var replacement = _store.Write(elements);
        _store.Read(replacement).Should().Equal(elements);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_applies_vector_reclaim_only_for_a_committed_transaction(bool commit)
    {
        string directory = Path.Combine(Path.GetTempPath(), "vector-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string metaPath = Path.Combine(directory, "metadata");
            string blobPath = Path.Combine(directory, "blob");
            float[] elements = Enumerable.Range(0, 4096).Select(i => (float)i).ToArray();
            VectorPayloadRef original;
            using (var metadata = new PagedFile(metaPath))
            using (var blob = new PagedFile(blobPath))
                original = new VectorPayloadStore(metadata, blob).Write(elements);
            string recoveredMeta = Path.Combine(directory, "recovered-metadata");
            string recoveredBlob = Path.Combine(directory, "recovered-blob");
            File.Copy(metaPath, recoveredMeta);
            File.Copy(blobPath, recoveredBlob);
            string walPath = Path.Combine(directory, "wal");
            using (var wal = new WriteAheadLog(walPath))
            using (var metadata = new PagedFile(metaPath))
            using (var blob = new PagedFile(blobPath))
            {
                var store = new VectorPayloadStore(metadata, blob);
                metadata.EnableWalLogging(1, wal);
                blob.EnableWalLogging(2, wal);
                var transaction = new TransactionId(1);
                wal.Append(WalRecordType.BeginWrite, transaction, ReadOnlySpan<byte>.Empty);
                var writes = new WalWriteSet(wal, transaction);
                wal.ActiveWriteSet = writes;
                store.Free(original).Should().BeTrue();
                writes.FlushPending();
                long lsn = commit ? wal.Append(WalRecordType.Commit, transaction, ReadOnlySpan<byte>.Empty)
                    : wal.CurrentLsn;
                wal.FlushTo(lsn);
                wal.ActiveWriteSet = null;
            }
            using (var wal = new WriteAheadLog(walPath))
            using (var metadata = new PagedFile(recoveredMeta))
            using (var blob = new PagedFile(recoveredBlob))
            {
                var recovery = new RecoveryManager(new NullPageManager(), wal,
                    new Dictionary<byte, IPagedFile> { { 1, metadata }, { 2, blob } });
                recovery.Recover();
                recovery.Recover();
                var recovered = new VectorPayloadStore(metadata, blob);
                if (commit)
                {
                    Action read = () => recovered.Read(original);
                    read.Should().Throw<CorruptionException>();
                    recovered.Free(original).Should().BeFalse();
                }
                else
                {
                    recovered.Read(original).Should().Equal(elements);
                    recovered.Free(original).Should().BeTrue();
                }
                long pages = blob.PageCount;
                var replacement = recovered.Write(elements);
                replacement.Sequence.Should().BeGreaterThan(original.Sequence);
                blob.PageCount.Should().Be(pages);
                recovered.Read(replacement).Should().Equal(elements);
                recovered.ScanOrphans(new HashSet<VectorPayloadRef> { replacement }).Should().BeEmpty();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class NullPageManager : IPageManager
    {
        public IPagedFile OpenOrCreate(string path, PageKind defaultKind) => throw new NotSupportedException();
        public void FlushAll() { }
        public void Dispose() { }
    }

    public void Dispose()
    {
        _metadata.Dispose();
        _blob.Dispose();
        File.Delete(_metadataPath);
        File.Delete(_blobPath);
    }
}
