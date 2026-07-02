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
    public void Golden_database_fixture_opens_and_validates_page_checksums()
    {
        string path = CopyFixtureToTemp(DatabaseFixtureName);
        byte[] fixture = File.ReadAllBytes(path);
        BinaryPrimitives.ReadUInt32LittleEndian(fixture.AsSpan(24)).Should().Be(0x461A_C369u);
        BinaryPrimitives.ReadUInt32LittleEndian(fixture.AsSpan(PagedFile.PageSizeConst + 24))
            .Should().Be(0x0351_505Cu);

        using var pagedFile = new PagedFile(path);
        pagedFile.PageCount.Should().Be(2);
        using var page = pagedFile.PinForRead(new PageId(1));
        BinaryPrimitives.ReadInt64LittleEndian(page.Data).Should().Be(0x0102_0304_0506_0708);
        page.Data[127].Should().Be(0xA5);
        page.Data[4095].Should().Be(0x5A);
    }

    [Fact]
    public void Golden_wal_fixture_opens_and_validates_record_checksums()
    {
        string path = CopyFixtureToTemp(WalFixtureName);
        byte[] fixture = File.ReadAllBytes(path);
        BinaryPrimitives.ReadUInt32LittleEndian(fixture.AsSpan(21)).Should().Be(0xD010_6524u);
        BinaryPrimitives.ReadUInt32LittleEndian(fixture.AsSpan(25 + 21)).Should().Be(0xB902_337Fu);
        BinaryPrimitives.ReadUInt32LittleEndian(fixture.AsSpan(55 + 21)).Should().Be(0x9D25_A459u);

        using var reader = new WalReader(path, startLsn: 0);
        reader.TryReadNext(out WalRecord begin).Should().BeTrue();
        begin.Lsn.Should().Be(0);
        begin.Type.Should().Be(WalRecordType.Begin);
        begin.TransactionId.Should().Be(new TransactionId(42));

        reader.TryReadNext(out WalRecord pageImage).Should().BeTrue();
        pageImage.Lsn.Should().Be(1);
        pageImage.Type.Should().Be(WalRecordType.PageImage);
        pageImage.Payload.ToArray().Should().Equal(0x10, 0x20, 0x30, 0x40, 0x50);

        reader.TryReadNext(out WalRecord commit).Should().BeTrue();
        commit.Lsn.Should().Be(2);
        commit.Type.Should().Be(WalRecordType.Commit);
        reader.TryReadNext(out _).Should().BeFalse();
    }

    [Fact]
    public void Golden_fixtures_match_current_writer()
    {
        byte[] database = CreateDatabaseFixture();
        byte[] wal = CreateWalFixture();
        string databasePath = FixturePath(DatabaseFixtureName);
        string walPath = FixturePath(WalFixtureName);

        if (Environment.GetEnvironmentVariable("QUIVER_UPDATE_CRC_GOLDENS") == "1")
        {
            string fixtureDirectory = FindFixtureSourceDirectory();
            Directory.CreateDirectory(fixtureDirectory);
            databasePath = Path.Combine(fixtureDirectory, DatabaseFixtureName);
            walPath = Path.Combine(fixtureDirectory, WalFixtureName);
            File.WriteAllBytes(databasePath, database);
            File.WriteAllBytes(walPath, wal);
        }

        File.ReadAllBytes(databasePath).Should().Equal(database);
        File.ReadAllBytes(walPath).Should().Equal(wal);
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
            PageId pageId = pagedFile.AllocatePage(PageKind.NodeRecord);
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
            wal.Append(WalRecordType.Begin, tx, []);
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

    private static string FindFixtureSourceDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quiver.slnx")))
            directory = directory.Parent;

        if (directory is null)
            throw new InvalidOperationException("Could not find the Quiver repository root.");

        return Path.Combine(directory.FullName, "tests", "Quiver.Storage.Tests", "Fixtures");
    }
}
