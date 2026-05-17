namespace Quiver.Client;

/// <summary>
/// 型付きトラバーサル結果のストリーミングカーソル。
/// 所属トランザクションが生きている間だけ有効で、呼び出し側が必ず破棄する責任を負う。
/// </summary>
/// <typeparam name="T">列挙する要素型。</typeparam>
public interface ITraversalCursor<out T> : IDisposable
{
    /// <summary>次の要素に進む。要素が無くなったら <c>false</c>。</summary>
    bool MoveNext();

    /// <summary>直近の <see cref="MoveNext"/> で取得した現在要素。</summary>
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
