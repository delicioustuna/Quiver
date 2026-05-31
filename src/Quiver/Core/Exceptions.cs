namespace Quiver.Core;

public abstract class GraphDbException : Exception
{
    protected GraphDbException(string message) : base(message) { }
    protected GraphDbException(string message, Exception inner) : base(message, inner) { }
}

public sealed class StorageException(string message, Exception? inner = null)
    : GraphDbException(message, inner!);

public sealed class TransactionException(string message, Exception? inner = null)
    : GraphDbException(message, inner!);

public sealed class ConstraintException(string message, Exception? inner = null)
    : GraphDbException(message, inner!);

public sealed class CorruptionException(string message, Exception? inner = null)
    : GraphDbException(message, inner!);
