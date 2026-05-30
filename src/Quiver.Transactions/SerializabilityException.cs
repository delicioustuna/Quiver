using Quiver.Core;

namespace Quiver.Transactions;

/// <summary>
/// FT-33: <see cref="IsolationLevel.Serializable"/> のトランザクションが SSN (Serial Safety Net) の
/// exclusion window 検証 (π(T) &gt; η(T)) に違反したときに投げられる例外。
///
/// <para>SSN は serializable 違反 (write skew / dangerous structure / read-only anomaly 等) を
/// commit 時、または read/write hook での early-abort 時に検出し、当該トランザクションを犠牲者として
/// abort する。捕捉した呼び出し側は <see cref="ITransaction.Abort"/> / <see cref="IDisposable.Dispose"/>
/// で終了させる責務がある。SSN は deadlock-free かつ abort 後の即時 retry が必ず成功する
/// (Wang et al. Theorem 7) ため、典型的な対処は「abort → 同一ロジックで retry」。</para>
///
/// <para>並列モデルは <see cref="DeadlockException"/> と同様 (<see cref="GraphDbException"/> 派生、
/// 犠牲 tx の識別子を保持)。</para>
/// </summary>
public sealed class SerializabilityException : GraphDbException
{
    /// <summary>直列化可能性違反で abort された tx 識別子。</summary>
    public TransactionId Victim { get; }

    internal SerializabilityException(TransactionId victim, string message) : base(message)
    {
        Victim = victim;
    }
}
