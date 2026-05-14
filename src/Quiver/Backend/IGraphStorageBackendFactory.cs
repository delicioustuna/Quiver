namespace Quiver;

/// <summary>
/// Factory used by <see cref="GraphDatabase.Open(string, GraphDatabaseOptions?)"/>
/// to construct the active backend. Allows callers to inject custom backends
/// (e.g. an in-memory factory in tests) without subclassing <see cref="GraphDatabase"/>.
/// </summary>
public interface IGraphStorageBackendFactory
{
    IGraphStorageBackend Open(string directoryPath, GraphDatabaseOptions options);
}
