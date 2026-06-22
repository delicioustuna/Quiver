namespace Quiver.Studio.Models;

public sealed class QueryResult
{
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; }
    public string? ScalarText { get; }
    public string? Error { get; }
    public TimeSpan Elapsed { get; }

    public bool IsError => Error is not null;
    public bool IsScalar => ScalarText is not null;
    public bool IsTabular => Columns.Count > 0;

    private QueryResult(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        string? scalarText,
        string? error,
        TimeSpan elapsed)
    {
        Columns = columns;
        Rows = rows;
        ScalarText = scalarText;
        Error = error;
        Elapsed = elapsed;
    }

    public static QueryResult Tabular(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        TimeSpan elapsed)
        => new(columns, rows, null, null, elapsed);

    public static QueryResult Scalar(string text, TimeSpan elapsed)
        => new([], [], text, null, elapsed);

    public static QueryResult Empty(TimeSpan elapsed)
        => new([], [], null, null, elapsed);

    public static QueryResult FromError(string error, TimeSpan elapsed)
        => new([], [], null, error, elapsed);
}
