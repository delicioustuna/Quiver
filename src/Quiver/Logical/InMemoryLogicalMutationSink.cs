using Quiver.Core;

namespace Quiver.Logical;

/// <summary>
/// Reference <see cref="ILogicalMutationSink"/> that keeps every committed
/// batch in memory in arrival order. Intended for tests, debug inspection,
/// and small audit scenarios — not for production replication.
/// </summary>
public sealed class InMemoryLogicalMutationSink : ILogicalMutationSink
{
    private readonly object _lock = new();
    private readonly List<CommittedBatch> _batches = new();

    public IReadOnlyList<CommittedBatch> Batches
    {
        get
        {
            lock (_lock) return _batches.ToArray();
        }
    }

    /// <summary>
    /// Flat enumeration over every mutation across every batch, in commit
    /// order. Convenient for tests that simply want the full replay stream.
    /// </summary>
    public IEnumerable<LogicalMutation> Mutations
    {
        get
        {
            lock (_lock)
            {
                foreach (var b in _batches)
                    foreach (var m in b.Mutations)
                        yield return m;
            }
        }
    }

    public void OnCommitted(TransactionId transactionId, IReadOnlyList<LogicalMutation> mutations)
    {
        lock (_lock)
            _batches.Add(new CommittedBatch(transactionId, mutations));
    }

    public void Clear()
    {
        lock (_lock) _batches.Clear();
    }

    public readonly record struct CommittedBatch(
        TransactionId TransactionId,
        IReadOnlyList<LogicalMutation> Mutations);
}
