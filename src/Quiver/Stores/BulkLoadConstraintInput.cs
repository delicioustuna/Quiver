namespace Quiver.Storage.Records;

internal readonly record struct BulkVertexEntry(long Id, int LabelId);

internal readonly record struct BulkPropertyEntry(
    int KeyId,
    PropertyValueType Type,
    long Scalar,
    byte[]? Data);

internal readonly record struct BulkLoadConstraintInput(
    IReadOnlyList<BulkVertexEntry> Vertices,
    IReadOnlyDictionary<long, List<BulkPropertyEntry>> PropertiesByVertex);
