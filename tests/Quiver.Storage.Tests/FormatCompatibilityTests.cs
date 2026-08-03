using System.Buffers.Binary;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage.Wal;
using Xunit;

namespace Quiver.Storage.Tests;

public sealed class FormatCompatibilityTests : IDisposable
{
    private const string PreviousReleaseFixtureName = "v0.5.0-current.quiver";
    private const byte OptionalExtensionTenantId = 0x3E;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "quiver-format-compatibility-" + Guid.NewGuid().ToString("N"));

    public FormatCompatibilityTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Previous_release_database_opens_and_accepts_a_new_commit()
    {
        string path = CopyFixtureToTemp(PreviousReleaseFixtureName);

        using (var database = QuiverDatabase.Open(path))
        {
            database.Diagnostics.GetStatistics().VertexCount.Should().Be(2);
            using var transaction = database.BeginWriteTransaction();
            transaction.CreateVertex("CurrentBuild");
            transaction.Commit();
        }

        using var reopened = QuiverDatabase.Open(path);
        reopened.Diagnostics.GetStatistics().VertexCount.Should().Be(3);
    }

    [Fact]
    public void Unknown_database_header_extension_is_rejected_before_creating_a_wal()
    {
        string path = CreateCurrentDatabase("unknown-extension.quiver");
        byte[] bytes = File.ReadAllBytes(path);
        Span<byte> metaPage = bytes.AsSpan(0, PagedFile.PageSizeConst);
        metaPage[11] = 1;
        PageHeader.UpdateLsnAndChecksum(metaPage, PageHeader.ReadLsn(metaPage));
        File.WriteAllBytes(path, bytes);
        byte[] beforeOpen = File.ReadAllBytes(path);

        Action open = () => QuiverDatabase.Open(path);

        open.Should().Throw<CorruptionException>()
            .WithMessage("*Unsupported database page header extension*");
        File.ReadAllBytes(path).Should().Equal(beforeOpen);
        File.Exists(path + "-wal").Should().BeFalse();
    }

    [Fact]
    public void Unsupported_database_family_is_rejected_before_creating_a_wal()
    {
        string path = CreateCurrentDatabase("unsupported-family.quiver");
        byte[] bytes = File.ReadAllBytes(path);
        bytes[9] = 1;
        File.WriteAllBytes(path, bytes);
        byte[] beforeOpen = File.ReadAllBytes(path);

        Action open = () => QuiverDatabase.Open(path);

        open.Should().Throw<StorageFormatMismatchException>();
        File.ReadAllBytes(path).Should().Equal(beforeOpen);
        File.Exists(path + "-wal").Should().BeFalse();
    }

    [Fact]
    public void Unknown_wal_record_is_rejected_before_database_or_wal_rewrite()
    {
        string path = CreateCurrentDatabase("unknown-record.quiver");
        string walPath = path + "-wal";
        using (var wal = new WriteAheadLog(walPath))
        {
            long lsn = wal.Append(WalRecordType.BeginWrite, new TransactionId(42), []);
            wal.FlushTo(lsn);
        }
        RewriteFirstRecordType(walPath, 0x7F);
        byte[] databaseBeforeOpen = File.ReadAllBytes(path);
        byte[] walBeforeOpen = File.ReadAllBytes(walPath);

        Action open = () => QuiverDatabase.Open(path);

        open.Should().Throw<CorruptionException>()
            .WithMessage("*Unknown WAL record type 0x7F*");
        File.ReadAllBytes(path).Should().Equal(databaseBeforeOpen);
        File.ReadAllBytes(walPath).Should().Equal(walBeforeOpen);
    }

    [Fact]
    public void Optional_unknown_tenant_is_preserved_by_current_build_writes()
    {
        string path = CreateCurrentDatabase("optional-extension.quiver");
        PageId extensionPage;
        using (var container = new SingleFileContainer(path))
        {
            IPagedFile extension = container.OpenTenant(
                OptionalExtensionTenantId,
                PageKind.Header);
            extensionPage = extension.AllocatePage(PageKind.Header);
            using var page = extension.PinForWrite(extensionPage);
            BinaryPrimitives.WriteInt64LittleEndian(page.Data, 0x1020_3040_5060_7080);
            container.Flush();
        }

        using (var database = QuiverDatabase.Open(path))
        {
            using var transaction = database.BeginWriteTransaction();
            transaction.CreateVertex("PreserveExtension");
            transaction.Commit();
        }

        using var reopenedContainer = new SingleFileContainer(path);
        reopenedContainer.HasTenant(OptionalExtensionTenantId).Should().BeTrue();
        IPagedFile reopenedExtension = reopenedContainer.OpenTenant(
            OptionalExtensionTenantId,
            PageKind.Header);
        using var reopenedPage = reopenedExtension.PinForRead(extensionPage);
        BinaryPrimitives.ReadInt64LittleEndian(reopenedPage.Data)
            .Should().Be(0x1020_3040_5060_7080);
    }

    private string CreateCurrentDatabase(string fileName)
    {
        string path = Path.Combine(_directory, fileName);
        using (var database = QuiverDatabase.Open(path))
        {
            using var transaction = database.BeginWriteTransaction();
            transaction.CreateVertex("Seed");
            transaction.Commit();
        }

        return path;
    }

    private string CopyFixtureToTemp(string fixtureName)
    {
        string destination = Path.Combine(_directory, fixtureName);
        File.Copy(FixturePath(fixtureName), destination);
        return destination;
    }

    private static string FixturePath(string fixtureName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);

    private static void RewriteFirstRecordType(string walFile, byte recordType)
    {
        byte[] bytes = File.ReadAllBytes(walFile);
        int recordOffset = WalFormat.FileHeaderSize;
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(recordOffset));
        bytes[recordOffset + 20] = recordType;

        var crc = new Crc32();
        crc.Append(bytes.AsSpan(recordOffset, 21));
        int payloadLength = length - WriteAheadLog.HeaderSize;
        if (payloadLength > 0)
            crc.Append(bytes.AsSpan(recordOffset + WriteAheadLog.HeaderSize, payloadLength));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(recordOffset + 21),
            crc.GetCurrentHashAsUInt32());

        File.WriteAllBytes(walFile, bytes);
    }
}
