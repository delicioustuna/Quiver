namespace GraphDb.Engine.Client;

/// <summary>
/// Streaming cursor over typed traversal results.
/// Valid only within the owning transaction; must be disposed by the caller.
/// </summary>
public interface ITraversalCursor<out T> : IDisposable
{
    bool MoveNext();
    T Current { get; }
}

internal sealed class TraversalCursor<T>(IQueryCursor inner, Func<QueryRow, T> projection)
    : ITraversalCursor<T>
{
    private T _current = default!;

    public bool MoveNext()
    {
        if (!inner.MoveNext()) return false;
        _current = projection(inner.Current);
        return true;
    }

    public T Current => _current;
    public void Dispose() => inner.Dispose();
}
