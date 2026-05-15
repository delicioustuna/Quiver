namespace Quiver.Transactions;

/// <summary>
/// Register callbacks that fire after a transaction's outcome is durable
/// (commit) or after it has been rolled back. Designed for VEC-3 so embedding
/// pipelines / cache invalidation / audit log shipping can hook into commit
/// completion without coupling to the transaction internals. Generic by
/// design — not embedding-specific.
/// </summary>
/// <remarks>
/// Semantics:
/// <list type="bullet">
/// <item><see cref="OnCommitted"/> fires only after the WAL flush
/// (or SQLite commit on the SQLite backend) returns successfully.
/// If commit fails, only <see cref="OnRolledBack"/> handlers run.</item>
/// <item>Handlers run in registration order. An exception in one handler is
/// caught and ignored so subsequent handlers still run and the transaction
/// outcome is not affected.</item>
/// <item>Handlers registered after the transaction has finished
/// (Committed / Aborted) are dispatched immediately to match the
/// already-resolved outcome.</item>
/// </list>
/// Crash recovery scenario: if the process dies between WAL fsync and the
/// hook firing, the hook is lost. Callers that need at-least-once delivery
/// (e.g. embedding task enqueue) must reconcile via a startup scan — see
/// VEC-4's <c>ScanAndEnqueueAsync</c>.
/// </remarks>
public interface ICommitHookRegistrar
{
    /// <summary>
    /// Register a callback to fire after the transaction has committed
    /// (WAL flush / SQLite commit complete).
    /// </summary>
    void OnCommitted(Action callback);

    /// <summary>
    /// Register a callback to fire after the transaction has been rolled
    /// back (explicit Abort, Dispose of an active transaction, or commit
    /// failure).
    /// </summary>
    void OnRolledBack(Action callback);
}
