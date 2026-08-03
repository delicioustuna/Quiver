using System.Buffers.Binary;
using FluentAssertions;
using Quiver.Core;
using Quiver.Storage;
using Xunit;

namespace Quiver.Index.Tests;

public sealed class IndexCatalogFormatTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "quiver_index_catalog_format_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Reopen_rejects_unknown_future_catalog_format()
    {
        using (var manager = IndexManager.OpenStandalone(_directory))
            _ = manager.CreateInt64Index("by_age");

        string path = Path.Combine(_directory, "graph.quiver");
        using (var container = new SingleFileContainer(path))
        {
            IPagedFile catalog = container.OpenTenant(
                IndexManager.CatalogTenantId,
                PageKind.Header);
            using (var page = catalog.PinForWrite(new PageId(1)))
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    page.Data[IndexManager.CatalogFormatVersionOffset..],
                    IndexManager.CatalogFormatVersion + 1);
            }
            container.Flush();
        }

        Action reopen = () =>
        {
            using var manager = IndexManager.OpenStandalone(_directory);
        };

        reopen.Should().Throw<StorageFormatMismatchException>()
            .Where(exception =>
                exception.FileKind == "indexcatalog"
                && exception.Found == IndexManager.CatalogFormatVersion + 1
                && exception.Expected == IndexManager.CatalogFormatVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
