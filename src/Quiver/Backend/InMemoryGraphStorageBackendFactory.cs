namespace Quiver;

/// <summary>
/// ファイルシステムを使用しないインメモリバックエンドを生成する。
/// </summary>
internal sealed class InMemoryGraphStorageBackendFactory : IGraphStorageBackendFactory
{
    public IGraphStorageBackend Open(string filePath, GraphDatabaseOptions options)
        => new BinaryGraphStorageBackendFactory().OpenInMemory(options);
}
