using Quiver.Stores;

namespace Quiver;

/// <summary>
/// Optional bulk-load entry points exposed by an <see cref="IGraphStorageBackend"/>.
/// Backends that do not support a given capability leave the corresponding delegate null.
/// </summary>
public sealed class BulkLoadCapabilities
{
    /// <summary>
    /// Begins a binary-backend bulk load. The boolean argument controls whether
    /// <see cref="BulkLoader.Commit"/> additionally builds adj.db / adj_idx.dat.
    /// Null when the active backend is not the binary backend.
    /// </summary>
    public Func<bool, BulkLoader>? BeginBinaryBulkLoad { get; init; }

    public bool SupportsBinaryBulkLoad => BeginBinaryBulkLoad is not null;
}
