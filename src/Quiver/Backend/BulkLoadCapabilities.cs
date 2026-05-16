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

    /// <summary>
    /// PW-9: begins a streaming binary-backend bulk load suited for 10M+ edge imports.
    /// Backend must produce a <see cref="StreamingBulkLoader"/> that streams relationship
    /// records to a temp file during append, then computes chain pointers via dense
    /// <c>long[]</c> arrays at commit time. The boolean controls whether the adjacency
    /// index is built. Null when the active backend has no streaming bulk-load path.
    /// </summary>
    public Func<bool, StreamingBulkLoader>? BeginStreamingBinaryBulkLoad { get; init; }

    public bool SupportsStreamingBinaryBulkLoad => BeginStreamingBinaryBulkLoad is not null;
}
