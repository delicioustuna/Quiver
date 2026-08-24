using System.Globalization;
using System.Text;
using System.Text.Json;
using Yatagarasu.Core;
using Yatagarasu.Storage.Records;

namespace Yatagarasu;

/// <summary>graph JSON に含める entity を指定する。</summary>
internal sealed class GraphSelection
{
    private GraphSelection(bool all)
    {
        IsAll = all;
    }

    /// <summary>
    /// Vertex、Edge、Nexus の任意の組を選択する。
    /// 明示Edgeの両端と明示Nexusの全メンバーをVertex集合へ加えた後、
    /// 最終Vertex集合により誘導されるrelationを出力する。
    /// </summary>
    /// <param name="vertices">選択するVertex。<c>null</c>は空集合。</param>
    /// <param name="edges">選択するEdge。<c>null</c>は空集合。</param>
    /// <param name="nexuses">選択するNexus。<c>null</c>は空集合。</param>
    public GraphSelection(
        IEnumerable<VertexId>? vertices = null,
        IEnumerable<EdgeId>? edges = null,
        IEnumerable<NexusId>? nexuses = null)
    {
        Vertices = vertices;
        Edges = edges;
        Nexuses = nexuses;
    }

    /// <summary>現在のsnapshotに可視なgraph全体を選択する。</summary>
    public static GraphSelection All { get; } = new(all: true);

    internal bool IsAll { get; }
    internal IEnumerable<VertexId>? Vertices { get; }
    internal IEnumerable<EdgeId>? Edges { get; }
    internal IEnumerable<NexusId>? Nexuses { get; }
}

/// <summary>graph JSON export の出力オプション。</summary>
internal sealed class GraphJsonExportOptions
{
    /// <summary>インデントと改行を付けたJSONを出力する。</summary>
    public bool WriteIndented { get; set; }

    /// <summary><see cref="PropertyValueType.Bytes"/>プロパティを出力する。</summary>
    public bool IncludeBytes { get; set; } = true;

    /// <summary><see cref="PropertyValueType.FloatArray"/>プロパティを出力する。</summary>
    public bool IncludeFloatArrays { get; set; } = true;

    /// <summary>
    /// 出力するプロパティキー名。<c>null</c>は全キーを表し、空集合は全プロパティを除外する。
    /// </summary>
    public IReadOnlyCollection<string>? IncludedPropertyKeys { get; set; }

    /// <summary>
    /// 永続IDを持たない旧DBをexportするときに使う出所ID。
    /// 未指定ならexport操作限りの一時IDを生成する。
    /// </summary>
    public DatabaseInstanceId? FallbackDatabaseInstanceId { get; set; }
}

/// <summary>graph JSON export の件数と出所情報。</summary>
/// <param name="SourceDatabaseId">JSONへ記録した出所データベースID。</param>
/// <param name="SnapshotId">このexport操作を識別するUUID。</param>
/// <param name="UsesTemporaryDatabaseId">永続またはcaller指定のIDではなく一時IDを使用したか。</param>
/// <param name="VertexCount">出力したVertex数。</param>
/// <param name="EdgeCount">出力したEdge数。</param>
/// <param name="NexusCount">出力したNexus数。</param>
internal readonly record struct GraphJsonExportResult(
    DatabaseInstanceId SourceDatabaseId,
    Guid SnapshotId,
    bool UsesTemporaryDatabaseId,
    long VertexCount,
    long EdgeCount,
    long NexusCount);

/// <summary>Yatagarasu graph を公開仕様のUTF-8 JSON文書へ逐次出力する。</summary>
internal static class GraphJsonExporter
{
    /// <summary>
    /// データベースの開始時snapshotを開き、graph JSON v1を出力する。
    /// 永続出所IDが無い旧DBを読むだけではDBを変更しない。
    /// </summary>
    public static GraphJsonExportResult Export(
        YatagarasuDatabase database,
        Stream destination,
        GraphSelection? selection = null,
        GraphJsonExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        options ??= new GraphJsonExportOptions();

        bool temporary = false;
        DatabaseInstanceId sourceDatabaseId;
        if (!database.TryGetDatabaseInstanceId(out sourceDatabaseId))
        {
            if (options.FallbackDatabaseInstanceId is { } fallback)
            {
                if (fallback.Value == Guid.Empty)
                    throw new ArgumentException(
                        "空のfallbackデータベースインスタンスIDは使用できません。",
                        nameof(options));
                sourceDatabaseId = fallback;
            }
            else
            {
                sourceDatabaseId = DatabaseInstanceId.New();
                temporary = true;
            }
        }

        using IReadTransaction transaction = database.BeginReadTransaction();
        return ExportCore(
            transaction,
            destination,
            sourceDatabaseId,
            temporary,
            selection ?? GraphSelection.All,
            options,
            cancellationToken);
    }

    /// <summary>
    /// callerが所有するread transactionのsnapshotをgraph JSON v1へ出力する。
    /// 出所IDはcallerが明示し、トランザクションの寿命もcallerが管理する。
    /// </summary>
    public static GraphJsonExportResult Export(
        IReadTransaction transaction,
        Stream destination,
        DatabaseInstanceId sourceDatabaseId,
        GraphSelection? selection = null,
        GraphJsonExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (sourceDatabaseId.Value == Guid.Empty)
            throw new ArgumentException("空のデータベースインスタンスIDは使用できません。", nameof(sourceDatabaseId));
        return ExportCore(
            transaction,
            destination,
            sourceDatabaseId,
            temporaryDatabaseId: false,
            selection ?? GraphSelection.All,
            options ?? new GraphJsonExportOptions(),
            cancellationToken);
    }

    private static GraphJsonExportResult ExportCore(
        IReadTransaction transaction,
        Stream destination,
        DatabaseInstanceId sourceDatabaseId,
        bool temporaryDatabaseId,
        GraphSelection selection,
        GraphJsonExportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("書き込み可能なストリームが必要です。", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();

        var includedPropertyKeys = options.IncludedPropertyKeys is null
            ? null
            : new HashSet<string>(options.IncludedPropertyKeys, StringComparer.Ordinal);
        Dictionary<int, string> propertyNames = BuildPropertyNameMap(transaction.Schema);
        Guid snapshotId = Guid.NewGuid();
        HashSet<VertexId>? selectedVertices = selection.IsAll
            ? null
            : ResolveVertexClosure(transaction, selection, cancellationToken);

        long vertexCount = 0;
        long edgeCount = 0;
        long nexusCount = 0;
        using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions
        {
            Indented = options.WriteIndented,
        });

        writer.WriteStartObject();
        writer.WriteString("format", "yatagarasu-graph");
        writer.WriteNumber("version", 1);
        writer.WriteStartObject("source");
        writer.WriteString("databaseId", sourceDatabaseId.Value);
        writer.WriteString("snapshot", snapshotId);
        writer.WriteEndObject();
        WriteSchema(writer, transaction.Schema, includedPropertyKeys, cancellationToken);

        writer.WriteStartArray("vertices");
        IEnumerable<VertexId> vertices = selectedVertices is null
            ? transaction.Query.Vertices().AsEnumerable()
            : selectedVertices.OrderBy(static id => id.Value);
        foreach (VertexId vertexId in vertices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? label = transaction.GetVertexLabel(vertexId);
            if (label is null)
                continue;
            WriteVertex(
                writer,
                new GraphJsonVertexRecord(
                    vertexId.Value,
                    label,
                    CaptureProperties(
                        transaction.EnumerateProperties(vertexId),
                        propertyNames,
                        includedPropertyKeys,
                        options,
                        cancellationToken)),
                cancellationToken);
            vertexCount++;
        }
        writer.WriteEndArray();

        writer.WriteStartArray("edges");
        foreach (EdgeId edgeId in transaction.Query.Edges().AsEnumerable())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!transaction.TryGetEdge(edgeId, out EdgeInfo edge)
                || selectedVertices is not null
                    && (!selectedVertices.Contains(edge.Source)
                        || !selectedVertices.Contains(edge.Target)))
                continue;

            WriteEdge(
                writer,
                new GraphJsonEdgeRecord(
                    edge.Id.Value,
                    edge.Source.Value,
                    edge.Target.Value,
                    edge.Type,
                    CaptureProperties(
                        transaction.EnumerateProperties(edge.Id),
                        propertyNames,
                        includedPropertyKeys,
                        options,
                        cancellationToken)),
                cancellationToken);
            edgeCount++;
        }
        writer.WriteEndArray();

        writer.WriteStartArray("nexuses");
        foreach (NexusId nexusId in transaction.Query.Nexuses().AsEnumerable())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? type = transaction.GetNexusType(nexusId);
            if (type is null)
                continue;
            GraphJsonMemberRecord[] members = CaptureMembers(
                transaction,
                nexusId,
                cancellationToken);
            if (selectedVertices is not null
                && members.Any(member => !selectedVertices.Contains(new VertexId(member.VertexId))))
                continue;

            WriteNexus(
                writer,
                new GraphJsonNexusRecord(
                    nexusId.Value,
                    type,
                    members,
                    CaptureProperties(
                        transaction.EnumerateProperties(nexusId),
                        propertyNames,
                        includedPropertyKeys,
                        options,
                        cancellationToken)),
                cancellationToken);
            nexusCount++;
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return new GraphJsonExportResult(
            sourceDatabaseId,
            snapshotId,
            temporaryDatabaseId,
            vertexCount,
            edgeCount,
            nexusCount);
    }

    private static HashSet<VertexId> ResolveVertexClosure(
        IReadTransaction transaction,
        GraphSelection selection,
        CancellationToken cancellationToken)
    {
        var vertices = new HashSet<VertexId>();
        if (selection.Vertices is not null)
        {
            foreach (VertexId vertexId in selection.Vertices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (transaction.VertexExists(vertexId))
                    vertices.Add(vertexId);
            }
        }

        if (selection.Edges is not null)
        {
            foreach (EdgeId edgeId in selection.Edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!transaction.TryGetEdge(edgeId, out EdgeInfo edge))
                    continue;
                vertices.Add(edge.Source);
                vertices.Add(edge.Target);
            }
        }

        if (selection.Nexuses is not null)
        {
            foreach (NexusId nexusId in selection.Nexuses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (transaction.GetNexusType(nexusId) is null)
                    continue;
                using NexusMemberEnumerator members = transaction.GetMembers(nexusId);
                while (members.MoveNext())
                    vertices.Add(members.Current.VertexId);
            }
        }

        return vertices;
    }

    private static Dictionary<int, string> BuildPropertyNameMap(ISchemaCatalog schema)
    {
        var names = new Dictionary<int, string>();
        foreach (string name in schema.ListPropertyKeys())
        {
            if (schema.TryGetPropertyKeyId(name, out PropertyKeyId id))
                names[id.Value] = name;
        }
        return names;
    }

    private static void WriteSchema(
        Utf8JsonWriter writer,
        ISchemaCatalog schema,
        IReadOnlySet<string>? includedPropertyKeys,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject("schema");
        WriteStringArray(writer, "labels", schema.ListLabels(), cancellationToken);
        WriteStringArray(writer, "edgeTypes", schema.ListEdgeTypes(), cancellationToken);
        WriteStringArray(writer, "nexusTypes", schema.ListNexusTypes(), cancellationToken);
        WriteStringArray(writer, "roles", schema.ListRoles(), cancellationToken);
        writer.WriteStartArray("propertyKeys");
        foreach (string name in schema.ListPropertyKeys().Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (includedPropertyKeys is not null && !includedPropertyKeys.Contains(name))
                continue;
            if (!schema.TryGetPropertyKeyId(name, out PropertyKeyId id))
                continue;
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString(
                "cardinality",
                CardinalityName(schema.GetPropertyKeyCardinality(id)));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteStringArray(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        writer.WriteStartArray(propertyName);
        foreach (string value in values.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }

    private static GraphJsonPropertyRecord[] CaptureProperties(
        PropertyCursor cursor,
        IReadOnlyDictionary<int, string> propertyNames,
        IReadOnlySet<string>? includedPropertyKeys,
        GraphJsonExportOptions options,
        CancellationToken cancellationToken)
    {
        var properties = new List<GraphJsonPropertyRecord>();
        using (cursor)
        {
            while (cursor.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                PropertyEntry entry = cursor.Current;
                if (!propertyNames.TryGetValue(entry.KeyId.Value, out string? key))
                    throw new InvalidDataException(
                        $"Property key token {entry.KeyId.Value} has no schema name.");
                if (includedPropertyKeys is not null && !includedPropertyKeys.Contains(key))
                    continue;
                if (!options.IncludeBytes && entry.Value.Type == PropertyValueType.Bytes)
                    continue;
                if (!options.IncludeFloatArrays && entry.Value.Type == PropertyValueType.FloatArray)
                    continue;
                properties.Add(GraphJsonPropertyRecord.Capture(
                    key,
                    entry.Cardinality,
                    entry.Value));
            }
        }
        return [.. properties];
    }

    private static GraphJsonMemberRecord[] CaptureMembers(
        IReadTransaction transaction,
        NexusId nexusId,
        CancellationToken cancellationToken)
    {
        var members = new List<GraphJsonMemberRecord>();
        using NexusMemberEnumerator cursor = transaction.GetMembers(nexusId);
        while (cursor.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            NexusMember member = cursor.Current;
            members.Add(new GraphJsonMemberRecord(member.Role, member.VertexId.Value));
        }
        return [.. members];
    }

    private static void WriteVertex(
        Utf8JsonWriter writer,
        GraphJsonVertexRecord vertex,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();
        WritePackedId(writer, "id", vertex.Id);
        writer.WriteString("label", vertex.Label);
        WriteProperties(writer, vertex.Properties, cancellationToken);
        writer.WriteEndObject();
    }

    private static void WriteEdge(
        Utf8JsonWriter writer,
        GraphJsonEdgeRecord edge,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();
        WritePackedId(writer, "id", edge.Id);
        writer.WriteString("type", edge.Type);
        WritePackedId(writer, "source", edge.Source);
        WritePackedId(writer, "target", edge.Target);
        WriteProperties(writer, edge.Properties, cancellationToken);
        writer.WriteEndObject();
    }

    private static void WriteNexus(
        Utf8JsonWriter writer,
        GraphJsonNexusRecord nexus,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();
        WritePackedId(writer, "id", nexus.Id);
        writer.WriteString("type", nexus.Type);
        writer.WriteStartArray("members");
        foreach (GraphJsonMemberRecord member in nexus.Members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteString("role", member.Role);
            WritePackedId(writer, "vertex", member.VertexId);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteProperties(writer, nexus.Properties, cancellationToken);
        writer.WriteEndObject();
    }

    private static void WriteProperties(
        Utf8JsonWriter writer,
        IReadOnlyList<GraphJsonPropertyRecord> properties,
        CancellationToken cancellationToken)
    {
        writer.WriteStartArray("properties");
        foreach (GraphJsonPropertyRecord property in properties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteString("key", property.Key);
            writer.WriteString("cardinality", CardinalityName(property.Cardinality));
            writer.WriteString("type", TypeName(property.Type));
            writer.WritePropertyName("value");
            WriteValue(writer, property, cancellationToken);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteValue(
        Utf8JsonWriter writer,
        GraphJsonPropertyRecord property,
        CancellationToken cancellationToken)
    {
        switch (property.Type)
        {
            case PropertyValueType.Bool:
                writer.WriteBooleanValue((bool)property.Value);
                break;
            case PropertyValueType.Int32:
                writer.WriteNumberValue((int)property.Value);
                break;
            case PropertyValueType.Int64:
                writer.WriteStringValue(((long)property.Value).ToString(CultureInfo.InvariantCulture));
                break;
            case PropertyValueType.Double:
                WriteDouble(writer, (double)property.Value);
                break;
            case PropertyValueType.String:
                writer.WriteStringValue((string)property.Value);
                break;
            case PropertyValueType.Bytes:
                writer.WriteBase64StringValue((byte[])property.Value);
                break;
            case PropertyValueType.FloatArray:
                writer.WriteStartArray();
                foreach (float value in (float[])property.Value)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteSingle(writer, value);
                }
                writer.WriteEndArray();
                break;
            default:
                throw new InvalidDataException($"Unsupported property type {property.Type}.");
        }
    }

    private static void WriteDouble(Utf8JsonWriter writer, double value)
    {
        if (double.IsNaN(value)) writer.WriteStringValue("NaN");
        else if (double.IsPositiveInfinity(value)) writer.WriteStringValue("Infinity");
        else if (double.IsNegativeInfinity(value)) writer.WriteStringValue("-Infinity");
        else writer.WriteNumberValue(value);
    }

    private static void WriteSingle(Utf8JsonWriter writer, float value)
    {
        if (float.IsNaN(value)) writer.WriteStringValue("NaN");
        else if (float.IsPositiveInfinity(value)) writer.WriteStringValue("Infinity");
        else if (float.IsNegativeInfinity(value)) writer.WriteStringValue("-Infinity");
        else writer.WriteNumberValue(value);
    }

    private static void WritePackedId(Utf8JsonWriter writer, string name, long value)
        => writer.WriteString(name, value.ToString(CultureInfo.InvariantCulture));

    private static string CardinalityName(PropertyCardinality cardinality) => cardinality switch
    {
        PropertyCardinality.Single => "single",
        PropertyCardinality.Set => "set",
        _ => throw new InvalidDataException($"Unsupported property cardinality {cardinality}."),
    };

    private static string TypeName(PropertyValueType type) => type switch
    {
        PropertyValueType.Bool => "bool",
        PropertyValueType.Int32 => "int32",
        PropertyValueType.Int64 => "int64",
        PropertyValueType.Double => "double",
        PropertyValueType.String => "string",
        PropertyValueType.Bytes => "bytes",
        PropertyValueType.FloatArray => "floatArray",
        _ => throw new InvalidDataException($"Unsupported property type {type}."),
    };
}

internal sealed record GraphJsonVertexRecord(
    long Id,
    string Label,
    GraphJsonPropertyRecord[] Properties);

internal sealed record GraphJsonEdgeRecord(
    long Id,
    long Source,
    long Target,
    string Type,
    GraphJsonPropertyRecord[] Properties);

internal sealed record GraphJsonNexusRecord(
    long Id,
    string Type,
    GraphJsonMemberRecord[] Members,
    GraphJsonPropertyRecord[] Properties);

internal sealed record GraphJsonMemberRecord(string Role, long VertexId);

internal sealed record GraphJsonPropertyRecord(
    string Key,
    PropertyCardinality Cardinality,
    PropertyValueType Type,
    object Value)
{
    internal static GraphJsonPropertyRecord Capture(
        string key,
        PropertyCardinality cardinality,
        PropertyValue value)
    {
        object heapValue = value.Type switch
        {
            PropertyValueType.Bool => value.BoolValue,
            PropertyValueType.Int32 => value.Int32Value,
            PropertyValueType.Int64 => value.Int64Value,
            PropertyValueType.Double => value.DoubleValue,
            PropertyValueType.String => Encoding.UTF8.GetString(value.Utf8StringValue),
            PropertyValueType.Bytes => value.BytesValue.ToArray(),
            PropertyValueType.FloatArray => value.FloatArrayValue.ToArray(),
            _ => throw new InvalidDataException($"Unsupported property type {value.Type}."),
        };
        return new GraphJsonPropertyRecord(key, cardinality, value.Type, heapValue);
    }
}
