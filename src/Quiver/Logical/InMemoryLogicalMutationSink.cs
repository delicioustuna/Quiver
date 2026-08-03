using Quiver.Core;

namespace Quiver.Logical;

/// <summary>
/// コミット済みバッチを到着順にメモリ保持する参照実装の <see cref="ILogicalMutationSink"/>。
/// テスト / デバッグ検査 / 小規模な監査向けで、本番のレプリケーション用途ではない。
/// </summary>
internal sealed class InMemoryLogicalMutationSink : ILogicalMutationSink
{
    private readonly object _lock = new();
    private readonly List<CommittedBatch> _batches = new();

    /// <summary>コミット順に保持されたバッチのスナップショット。</summary>
    public IReadOnlyList<CommittedBatch> Batches
    {
        get
        {
            lock (_lock) return _batches.ToArray();
        }
    }

    /// <summary>
    /// 全バッチの全ミューテーションをコミット順に平坦に列挙する。
    /// 再生ストリーム全体をそのまま欲しいテストに便利。
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

    /// <summary>コミット済みバッチを記録する (<see cref="ILogicalMutationSink"/> 実装)。</summary>
    public void OnCommitted(TransactionId transactionId, IReadOnlyList<LogicalMutation> mutations)
    {
        lock (_lock)
            _batches.Add(new CommittedBatch(transactionId, mutations));
    }

    /// <summary>保持している全バッチを破棄する。</summary>
    public void Clear()
    {
        lock (_lock) _batches.Clear();
    }

    /// <summary>1 トランザクション分のコミット済みミューテーション群。</summary>
    /// <param name="TransactionId">コミットされたトランザクション ID。</param>
    /// <param name="Mutations">そのトランザクションが生成したミューテーション列。</param>
    public readonly record struct CommittedBatch(
        TransactionId TransactionId,
        IReadOnlyList<LogicalMutation> Mutations);
}
