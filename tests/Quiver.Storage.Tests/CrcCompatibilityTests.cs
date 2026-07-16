using System.Buffers.Binary;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Wal;
using Xunit;
using ReferenceCrc32 = System.IO.Hashing.Crc32;
using QuiverCrc32 = Quiver.Core.Crc32;

namespace Quiver.Storage.Tests;

public sealed class CrcCompatibilityTests : IDisposable
{
    private const string DatabaseFixtureName = "crc-golden.quiver";
    private const string WalFixtureName = "crc-golden.quiver-wal";

    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "QuiverCrcTests_" + Guid.NewGuid().ToString("N"));

    public CrcCompatibilityTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); }
        catch { }
    }

    [Fact]
    public void Legacy_database_fixture_is_rejected()
    {
        string path = CopyFixtureToTemp(DatabaseFixtureName);
        Action open = () => new PagedFile(path);
        open.Should().Throw<StorageFormatMismatchException>();
    }

    [Fact]
    public void Legacy_wal_fixture_is_rejected()
    {
        string path = CopyFixtureToTemp(WalFixtureName);
        Action open = () => new WalReader(path, startLsn: 0);
        open.Should().Throw<WalFormatMismatchException>();
    }

    [Fact]
    public void Current_family_writer_round_trips_and_checksums()
    {
        byte[] database = CreateDatabaseFixture();
        byte[] wal = CreateWalFixture();
        database[..9].Should().Equal("QUIVER-SW"u8.ToArray());
        wal[..9].Should().Equal("QUIVER-SW"u8.ToArray());

        uint storedPageCrc = BinaryPrimitives.ReadUInt32LittleEndian(database.AsSpan(32));
        uint storedRecordCrc = BinaryPrimitives.ReadUInt32LittleEndian(wal.AsSpan(WalFormat.FileHeaderSize + 21));
        storedPageCrc.Should().NotBe(0);
        storedRecordCrc.Should().NotBe(0);

        string databasePath = Path.Combine(_tempDirectory, "roundtrip.quiver");
        string walPath = Path.Combine(_tempDirectory, "roundtrip.quiver-wal");
        File.WriteAllBytes(databasePath, database);
        File.WriteAllBytes(walPath, wal);

        using var pagedFile = new PagedFile(databasePath);
        using var pageHandle = pagedFile.PinForRead(new PageId(1));
        pageHandle.Data[127].Should().Be(0xA5);
        using var reader = new WalReader(walPath, 0);
        reader.TryReadNext(out var begin).Should().BeTrue();
        begin.Type.Should().Be(WalRecordType.BeginWrite);
    }

    [Fact]
    public void Reference_crc_matches_known_vector()
    {
        ReferenceCrc32.HashToUInt32("123456789"u8).Should().Be(0xCBF4_3926u);
        QuiverCrc32.HashToUInt32("123456789"u8).Should().Be(0xCBF4_3926u);
    }

    [Fact]
    public void One_shot_crc_matches_reference_across_lengths()
    {
        var random = new Random(0x5EED);
        var bytes = new byte[32_769];
        random.NextBytes(bytes);

        int[] lengths = [0, 1, 2, 7, 15, 16, 17, 31, 32, 255, 256, 8192, bytes.Length];
        foreach (int length in lengths)
        {
            QuiverCrc32.HashToUInt32(bytes.AsSpan(0, length))
                .Should().Be(ReferenceCrc32.HashToUInt32(bytes.AsSpan(0, length)),
                    $"length {length} must match System.IO.Hashing");
        }
    }

    [Fact]
    public void Incremental_crc_matches_reference_across_chunk_boundaries()
    {
        var random = new Random(0xC0FFEE);
        var bytes = new byte[16_411];
        random.NextBytes(bytes);
        int[] chunkSizes = [1, 3, 15, 16, 17, 127, 4096];

        foreach (int chunkSize in chunkSizes)
        {
            var actual = new QuiverCrc32();
            var expected = new ReferenceCrc32();
            for (int offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                int length = Math.Min(chunkSize, bytes.Length - offset);
                actual.Append(bytes.AsSpan(offset, length));
                expected.Append(bytes.AsSpan(offset, length));
            }

            actual.GetCurrentHashAsUInt32().Should().Be(expected.GetCurrentHashAsUInt32(),
                $"chunk size {chunkSize} must preserve incremental CRC state");
        }
    }

    private byte[] CreateDatabaseFixture()
    {
        string path = Path.Combine(_tempDirectory, "generated.quiver");
        using (var pagedFile = new PagedFile(path))
        {
            PageId pageId = pagedFile.AllocatePage(PageKind.VertexRecord);
            using var page = pagedFile.PinForWrite(pageId);
            BinaryPrimitives.WriteInt64LittleEndian(page.Data, 0x0102_0304_0506_0708);
            page.Data[127] = 0xA5;
            page.Data[4095] = 0x5A;
        }

        return File.ReadAllBytes(path).AsSpan(0, 2 * PagedFile.PageSizeConst).ToArray();
    }

    private byte[] CreateWalFixture()
    {
        string path = Path.Combine(_tempDirectory, "generated.quiver-wal");
        using (var wal = new WriteAheadLog(path))
        {
            var tx = new TransactionId(42);
            wal.Append(WalRecordType.BeginWrite, tx, []);
            wal.Append(WalRecordType.PageImage, tx, [0x10, 0x20, 0x30, 0x40, 0x50]);
            long commitLsn = wal.Append(WalRecordType.Commit, tx, []);
            wal.FlushTo(commitLsn);
        }

        return File.ReadAllBytes(path);
    }

    private string CopyFixtureToTemp(string fixtureName)
    {
        string destination = Path.Combine(_tempDirectory, fixtureName);
        File.Copy(FixturePath(fixtureName), destination);
        return destination;
    }

    private static string FixturePath(string fixtureName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);

}
