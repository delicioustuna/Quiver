namespace Quiver.Transactions;

/// <summary>
/// ロック取得モード。<see cref="LockManager"/> に渡す。
/// </summary>
public enum LockMode : byte
{
    /// <summary>共有ロック (S)。複数 tx で同時保持可。<see cref="Exclusive"/> とは排他。</summary>
    Shared = 1,
    /// <summary>排他ロック (X)。単一 tx のみ保持。他の S/X すべてと排他。</summary>
    Exclusive = 2,
}

/// <summary>
/// <see cref="Quiver.GraphDatabaseOptions"/> のロック戦略。
/// <see cref="ExclusiveOnly"/> は読み取りロック無し (現挙動の互換維持)、
/// <see cref="ReaderWriter"/> は read を <see cref="LockMode.Shared"/> で取り writer と分離する。
/// </summary>
public enum LockingMode : byte
{
    /// <summary>読み取りはロックを取らず、書き込みのみ <see cref="LockMode.Exclusive"/>。既定。</summary>
    ExclusiveOnly = 0,
    /// <summary>読み取り <see cref="LockMode.Shared"/> + 書き込み <see cref="LockMode.Exclusive"/>。</summary>
    ReaderWriter = 1,
}
