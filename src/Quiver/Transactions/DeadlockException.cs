using Quiver.Core;

namespace Quiver.Transactions;

/// <summary>
/// <see cref="DeadlockDetector"/> が wait-for graph に閉路を検出し、
/// 当該トランザクションを犠牲者として中断したときに、待機中だった lock 取得呼び出しから
/// 投げられる例外。捕捉した呼び出し側はトランザクションを <see cref="ITransaction.Abort"/>
/// / <see cref="IDisposable.Dispose"/> して終了させる責務がある (再試行は呼び出し側の判断)。
/// </summary>
public sealed class DeadlockException : GraphDbException
{
    /// <summary>犠牲者として選ばれた tx 識別子。</summary>
    public TransactionId Victim { get; }

    internal DeadlockException(TransactionId victim, string message) : base(message)
    {
        Victim = victim;
    }
}
