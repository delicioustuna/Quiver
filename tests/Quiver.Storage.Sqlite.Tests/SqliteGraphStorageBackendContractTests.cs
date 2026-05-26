using Quiver.Backend.Tests;
using Quiver.Storage.Sqlite;

namespace Quiver.Storage.Sqlite.Tests;

/// <summary>
/// Runs the shared BA-2 backend contract suite against the SQLite backend (BA-5).
/// Demonstrates that <see cref="GraphStorageBackendContractTests"/> can be applied
/// to any <see cref="IGraphStorageBackendFactory"/> implementation with no fixture
/// changes — exactly the property BA-2 was designed to give us.
/// </summary>
public sealed class SqliteGraphStorageBackendContractTests : GraphStorageBackendContractTests
{
    protected override IGraphStorageBackendFactory CreateFactory()
        => new SqliteGraphStorageBackendFactory();
}
