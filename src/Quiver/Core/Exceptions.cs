namespace Quiver.Core;

/// <summary>Quiver が投げる全例外の基底型。</summary>
public abstract class QuiverException : Exception
{
    /// <summary>メッセージを指定して例外を生成する。</summary>
    /// <param name="message">エラーメッセージ。</param>
    protected QuiverException(string message) : base(message) { }
    /// <summary>メッセージと内部例外を指定して例外を生成する。</summary>
    /// <param name="message">エラーメッセージ。</param>
    /// <param name="inner">原因となった内部例外。</param>
    protected QuiverException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>ストレージ層 (ページ / ファイル I/O) の失敗を表す例外。</summary>
/// <param name="message">エラーメッセージ。</param>
/// <param name="inner">原因となった内部例外 (任意)。</param>
public sealed class StorageException(string message, Exception? inner = null)
    : QuiverException(message, inner!);

/// <summary>トランザクションの不正操作 (非アクティブ tx の利用、コミット失敗等) を表す例外。</summary>
/// <param name="message">エラーメッセージ。</param>
/// <param name="inner">原因となった内部例外 (任意)。</param>
public sealed class TransactionException(string message, Exception? inner = null)
    : QuiverException(message, inner!);

/// <summary>同じトランザクションハンドルが同時に使用された場合の例外です。</summary>
public sealed class ConcurrentTransactionUseException : QuiverException
{
    /// <summary>競合したトランザクションIDを指定して例外を生成します。</summary>
    public ConcurrentTransactionUseException(TransactionId transactionId)
        : base($"Transaction {transactionId.Value} is already in use by another execution flow.")
    {
        TransactionId = transactionId;
    }

    /// <summary>競合したトランザクションID。</summary>
    public TransactionId TransactionId { get; }
}

/// <summary>
/// 未コミットページをディスクへ退避せずに保持できる安全容量を、
/// 一つの書き込みトランザクションが超えた場合の例外です。
/// </summary>
public sealed class TransactionTooLargeException : QuiverException
{
    /// <summary>対象トランザクションとバッファプール容量を指定して例外を生成します。</summary>
    /// <param name="transactionId">容量を超えたトランザクションID。</param>
    /// <param name="bufferPoolPageCapacity">利用可能なバッファプールのページ数。</param>
    public TransactionTooLargeException(
        TransactionId transactionId,
        int bufferPoolPageCapacity)
        : base(
            $"Transaction {transactionId.Value} exceeded the no-steal buffer capacity " +
            $"of {bufferPoolPageCapacity} pages.")
    {
        TransactionId = transactionId;
        BufferPoolPageCapacity = bufferPoolPageCapacity;
    }

    /// <summary>容量を超えたトランザクションID。</summary>
    public TransactionId TransactionId { get; }

    /// <summary>超過判定に使用したバッファプールのページ数。</summary>
    public int BufferPoolPageCapacity { get; }
}

/// <summary>制約違反 (一意制約など) を表す例外。</summary>
/// <param name="message">エラーメッセージ。</param>
/// <param name="inner">原因となった内部例外 (任意)。</param>
public sealed class ConstraintException(string message, Exception? inner = null)
    : QuiverException(message, inner!);

/// <summary>データ破損 (チェックサム不一致、不正なレコード等) を検出したことを表す例外。</summary>
/// <param name="message">エラーメッセージ。</param>
/// <param name="inner">原因となった内部例外 (任意)。</param>
public sealed class CorruptionException(string message, Exception? inner = null)
    : QuiverException(message, inner!);
