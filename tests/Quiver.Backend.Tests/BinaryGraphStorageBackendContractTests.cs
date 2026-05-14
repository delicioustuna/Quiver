namespace Quiver.Backend.Tests;

/// <summary>
/// Runs the BA-2 backend contract suite against the default binary backend.
/// When new backends are added (e.g. SQLite in BA-5), add a sibling subclass
/// that returns the matching factory.
/// </summary>
public sealed class BinaryGraphStorageBackendContractTests : GraphStorageBackendContractTests
{
    protected override IGraphStorageBackendFactory CreateFactory()
        => new BinaryGraphStorageBackendFactory();
}
