using Quiver.Core;

namespace Quiver.Logical;

/// <summary>
/// Receives the logical mutation stream of a
/// committed transaction. The sink fires exactly once per successful commit,
/// after the underlying physical durability boundary (binary backend: WAL flush;
/// SQLite backend: <c>COMMIT</c>) has returned successfully. Rolled-back
/// transactions are not delivered.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be thread-safe — although Quiver currently writes
/// from a single thread, multiple concurrent read-only transactions exist and
/// future write parallelism is permitted by the contract. Long / blocking
/// work should be queued out-of-band; the publisher does not pin the
/// transaction beyond the hand-off.
/// </para>
/// <para>
/// Exceptions raised by the sink are swallowed: a faulty audit sink must not
/// be allowed to mask a successful commit. Sinks that need at-least-once
/// delivery should persist before returning and use their own reconciliation
/// at startup.
/// </para>
/// </remarks>
public interface ILogicalMutationSink
{
    /// <summary>
    /// Invoked once per committed transaction with the ordered list of
    /// mutations that produced the new state. The list is owned by the sink
    /// after the call and may be retained.
    /// </summary>
    void OnCommitted(TransactionId transactionId, IReadOnlyList<LogicalMutation> mutations);
}
