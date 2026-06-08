namespace Quiver.Core;

/// <summary>Quiver が投げる全例外の基底型。</summary>
public abstract class GraphDbException : Exception
{
    /// <summary>メッセージを指定して例外を生成する。</summary>
    /// <param name="message">エラーメッセージ。</param>
    protected GraphDbException(string message) : base(message) { }
    /// <summary>メッセージと内部例外を指定して例外を生成する。</summary>
    /// <param name="message">エラーメッセージ。</param>
    /// <param name="inner">原因となった内部例外。</param>
    protected GraphDbException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>ストレージ層 (ページ / ファイル I/O) の失敗を表す例外。</summary>
/// <param name="message">エラーメッセージ。</param>
/// <param name="inner">原因となった内部例外 (任意)。</param>
public sealed class StorageException(string message, Exception? inner = null)
    : GraphDbException(message, inner!);

/// <summary>トランザクションの不正操作 (非アクティブ tx の利用、コミット失敗等) を表す例外。</summary>
/// <param name="message">エラーメッセージ。</param>
/// <param name="inner">原因となった内部例外 (任意)。</param>
public sealed class TransactionException(string message, Exception? inner = null)
    : GraphDbException(message, inner!);

/// <summary>制約違反 (一意制約など) を表す例外。</summary>
/// <param name="message">エラーメッセージ。</param>
/// <param name="inner">原因となった内部例外 (任意)。</param>
public sealed class ConstraintException(string message, Exception? inner = null)
    : GraphDbException(message, inner!);

/// <summary>データ破損 (チェックサム不一致、不正なレコード等) を検出したことを表す例外。</summary>
/// <param name="message">エラーメッセージ。</param>
/// <param name="inner">原因となった内部例外 (任意)。</param>
public sealed class CorruptionException(string message, Exception? inner = null)
    : GraphDbException(message, inner!);
