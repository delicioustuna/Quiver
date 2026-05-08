using GraphDb.Engine.Core;

namespace GraphDb.Engine;

public interface ISchemaApi
{
    LabelId GetOrCreateLabel(string name);
    RelationshipTypeId GetOrCreateRelationshipType(string name);
    PropertyKeyId GetOrCreatePropertyKey(string name);

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
