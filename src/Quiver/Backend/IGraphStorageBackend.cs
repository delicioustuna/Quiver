using Quiver.Core;
using Quiver.Transactions;

namespace Quiver;

/// <summary>
/// Backend-side contract for a Quiver storage engine. Each backend (binary,
/// SQLite, etc.) implements this interface so that <see cref="GraphDatabase"/>
/// stays a thin facade and storage layouts can be swapped at the factory level.
/// </summary>
public interface IGraphStorageBackend : IDisposable
{
    ITransactionManager Transactions { get; }
    ISchemaApi Schema { get; }
    IDiagnosticsApi Diagnostics { get; }
    IGraphAccessMethods Access { get; }
    BulkLoadCapabilities BulkLoad { get; }

    /// <summary>
    /// VEC-5: vector store used by <see cref="IGraphAccessMethods.KnnSearch"/>
    /// and exposed to users for <c>CreateVectorIndex</c> / <c>SetVector</c>.
    /// Defaults to an in-memory store on backends that don't yet persist
    /// vectors durably (binary, SQLite MVP).
    /// </summary>
    IVectorStore Vectors { get; }

    /// <summary>
    /// Begins a new transaction wrapped in an <see cref="IGraphTransaction"/>
    /// so token resolution is consistent with the active backend.
    /// </summary>
    IGraphTransaction BeginGraphTransaction(IsolationLevel level, bool readOnly);
}
