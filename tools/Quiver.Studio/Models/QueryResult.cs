using Quiver.Core;

namespace Quiver.Studio.Models;

public sealed class QueryResult
{
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; }
    public string? ScalarText { get; }
    public string? Error { get; }
    public TimeSpan Elapsed { get; }
    public IReadOnlyList<NodeId> ExtractedNodeIds { get; }
    public IReadOnlyList<RelationshipId> ExtractedRelationshipIds { get; }
    public IReadOnlyDictionary<long, float>? VectorScores { get; }

    public bool IsError => Error is not null;
    public bool IsScalar => ScalarText is not null;
    public bool IsTabular => Columns.Count > 0;
    public bool HasGraphData => ExtractedNodeIds.Count > 0 || ExtractedRelationshipIds.Count > 0;

    private QueryResult(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        string? scalarText,
        string? error,
        TimeSpan elapsed,
        IReadOnlyList<NodeId>? nodeIds = null,
        IReadOnlyList<RelationshipId>? relIds = null,
        IReadOnlyDictionary<long, float>? vectorScores = null)
    {
        Columns = columns;
        Rows = rows;
        ScalarText = scalarText;
        Error = error;
        Elapsed = elapsed;
        ExtractedNodeIds = nodeIds ?? [];
        ExtractedRelationshipIds = relIds ?? [];
        VectorScores = vectorScores;
    }

    public static QueryResult Tabular(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        TimeSpan elapsed,
        IReadOnlyList<NodeId>? nodeIds = null,
        IReadOnlyList<RelationshipId>? relIds = null,
        IReadOnlyDictionary<long, float>? vectorScores = null)
        => new(columns, rows, null, null, elapsed, nodeIds, relIds, vectorScores);

    public static QueryResult Scalar(string text, TimeSpan elapsed,
        IReadOnlyList<NodeId>? nodeIds = null,
        IReadOnlyList<RelationshipId>? relIds = null)
        => new([], [], text, null, elapsed, nodeIds, relIds);

    public static QueryResult Empty(TimeSpan elapsed)
        => new([], [], null, null, elapsed);

    public static QueryResult FromError(string error, TimeSpan elapsed)
        => new([], [], null, error, elapsed);
}
