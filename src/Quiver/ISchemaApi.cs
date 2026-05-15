using Quiver.Core;

namespace Quiver;

public interface ISchemaApi
{
    LabelId GetOrCreateLabel(string name);
    RelationshipTypeId GetOrCreateRelationshipType(string name);
    PropertyKeyId GetOrCreatePropertyKey(string name);

    /// <summary>
    /// GC-1: reverse lookup for label names. Returns null when the id was
    /// never registered. Used by Gremlin's <c>.label()</c> step and any
    /// diagnostic surface that wants to render an id back to its source name.
    /// </summary>
    string? GetLabelName(LabelId id);

    void CreateIndex(string indexName, string label, string propertyKey, IndexKind kind);
    void DropIndex(string indexName);
    IReadOnlyList<IndexInfo> ListIndexes();
}

public enum IndexKind
{
    Int32Equality,
    Int64Equality,
    DoubleEquality,
    StringEquality,
    StringRange,
}

public sealed record IndexInfo(
    string Name,
    string Label,
    string PropertyKey,
    IndexKind Kind,
    long EntryCount);
