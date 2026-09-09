using FluentAssertions;
using Yatagarasu.Core;
using Yatagarasu.Index.FullText;
using Xunit;

namespace Yatagarasu.Tests;

public sealed class FullTextDryRunCorruptionTests
{
    [Fact]
    public void Dry_run_reports_corrupt_manifest_without_rebuild_notifications()
    {
        int rebuilds = 0;
        int changes = 0;
        using var index = new FullTextSegmentIndex(null, _ => throw new NotSupportedException(),
            _ => rebuilds++, null!, null!, null!, null!);
        index.CatalogStateChanged = (_, _) => changes++;
        var definition = new FullTextIndexDefinition("body", new(PropertyOwnerKind.Vertex, "body", "Document"), "standard");
        var catalog = new FullTextCatalogEntry("body", FullTextDefinitionCodec.Encode(definition), "body", "standard",
            IndexLifecycleState.Ready, "invalid-manifest");
        Action dry = () => index.CollectGarbage(long.MaxValue, true, [catalog]);
        dry.Should().Throw<CorruptionException>();
        dry.Should().Throw<CorruptionException>();
        rebuilds.Should().Be(0);
        changes.Should().Be(0);
        index.CollectGarbage(long.MaxValue, false, [catalog]);
        rebuilds.Should().Be(1);
        changes.Should().Be(1);
    }
}
