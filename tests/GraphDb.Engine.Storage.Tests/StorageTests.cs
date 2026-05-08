using Xunit;
using GraphDb.Engine.Storage;
using FluentAssertions;

namespace GraphDb.Engine.Storage.Tests;

public class StorageTests
{
    [Fact]
    public void PageSize_Is8192()
    {
        PagedFile.PageSizeConst.Should().Be(8192);
    }
}
