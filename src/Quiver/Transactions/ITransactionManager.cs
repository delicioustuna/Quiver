namespace Quiver.Transactions;

/// <summary>内部トランザクション管理の入口です。</summary>
internal interface ITransactionManager : IDisposable
{
    /// <summary>読み取り専用スナップショットを開始します。</summary>
    ITransaction BeginRead();

    /// <summary>書き込み所有権を取得してトランザクションを開始します。</summary>
    ITransaction BeginWrite();

    /// <summary>現在実行中の読み取りと書き込みの合計数。</summary>
    int ActiveCount { get; }

    /// <summary>最古アクティブトランザクションのLSN。</summary>
    long OldestActiveLsn { get; }
}
