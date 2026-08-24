namespace Yatagarasu.Transactions;

/// <summary>
/// トランザクション内のセーブポイントを識別する不変ハンドル。
/// <see cref="ITransaction.Savepoint"/> が発行し、<see cref="ITransaction.RollbackTo"/> /
/// <see cref="ITransaction.ReleaseSavepoint"/> のターゲットとして渡す。
/// 異なるトランザクション間や、解放/ロールバック後の savepoint id を使うと
/// <see cref="Yatagarasu.Core.TransactionException"/>がスローされる。
/// </summary>
public readonly record struct SavepointId(long Value, string? Name = null)
{
    /// <summary>診断用文字列表現。名前が付いている場合は名前を含む。</summary>
    public override string ToString() => Name is null ? $"SP#{Value}" : $"SP#{Value}({Name})";
}
