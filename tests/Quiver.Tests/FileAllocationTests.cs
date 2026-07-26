using FluentAssertions;
using Xunit;

namespace Quiver.Tests;

public sealed class FileAllocationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "quiver_allocation_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Database_options_control_initial_allocation_and_growth_cap()
    {
        string path = Path.Combine(_directory, "graph.quiver");
        var options = new QuiverDatabaseOptions
        {
            InitialFileAllocationBytes = 2L * 1024 * 1024,
            MaximumFileGrowthStepBytes = 8L * 1024 * 1024,
        };

        using var database = QuiverDatabase.Open(path, options);
        new FileInfo(path).Length.Should().Be(2L * 1024 * 1024);
        (new FileInfo(path).Length % 8192).Should().Be(0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
