using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver;

/// <summary>graph JSONの構文、schema、参照、または重複定義が不正な場合の例外。</summary>
public sealed class GraphJsonImportException : Exception
{
    /// <summary>入力列における0始まりの文書位置。</summary>
    public int DocumentIndex { get; }

    /// <summary>判定できる場合のUTF-8文書先頭からのbyte位置。</summary>
    public long? ByteOffset { get; }

    /// <summary>詳細、文書位置、byte位置、原因を指定して例外を作成する。</summary>
    public GraphJsonImportException(
        string message,
        int documentIndex,
        long? byteOffset = null,
        Exception? innerException = null)
        : base(FormatMessage(message, documentIndex, byteOffset), innerException)
    {
        DocumentIndex = documentIndex;
        ByteOffset = byteOffset;
    }

    private static string FormatMessage(string message, int documentIndex, long? byteOffset)
        => byteOffset is { } offset
            ? $"graph JSON文書 {documentIndex}、byte {offset}: {message}"
            : $"graph JSON文書 {documentIndex}: {message}";
}

/// <summary>source entityとimport先の暫定IDの対応。</summary>
/// <param name="SourceDatabaseId">source文書のデータベースインスタンスID。</param>
/// <param name="Kind">entity種別。</param>
/// <param name="SourcePackedId">generationを含むsource packed ID。</param>
/// <param name="ProvisionalTargetPackedId">commit前のimport先packed ID。rollback後は無効。</param>
public readonly record struct GraphJsonImportMapping(
    DatabaseInstanceId SourceDatabaseId,
    EntityKind Kind,
    long SourcePackedId,
    long ProvisionalTargetPackedId);

/// <summary>graph JSON importのオプション。</summary>
public sealed partial class GraphJsonImportOptions
{
    /// <summary>
    /// source entityごとに一度呼ばれる対応通知。
    /// 通知されるtarget IDはcallerがtransactionをcommitするまで暫定値である。
    /// </summary>
    public Action<GraphJsonImportMapping>? MappingSink { get; set; }

    /// <summary>
    /// 既存target graphとの明示的な意味的merge。<c>null</c>ではsource identity単位のappend / unionを行う。
    /// 有効時は決定順のproperty適用まで全source property payloadを保持し、targetを一度走査して
    /// operation-local候補indexを構築するため、通常importより大きな作業メモリを必要とする。
    /// </summary>
    public GraphJsonSemanticMergeOptions? SemanticMerge { get; set; }
}

/// <summary>graph JSON importで新規作成または重複排除した件数。</summary>
/// <param name="DocumentCount">読み込んだJSON文書数。</param>
/// <param name="VertexCount">新規作成したVertex数。</param>
/// <param name="EdgeCount">新規作成したEdge数。</param>
/// <param name="NexusCount">新規作成したNexus数。</param>
/// <param name="DuplicateEntityCount">同じsource identityと同じ定義として再利用したentity数。</param>
/// <param name="SemanticMatchCount">意味的照合で既存または同operation内の先行targetを再利用したentity数。</param>
public readonly record struct GraphJsonImportResult(
    int DocumentCount,
    long VertexCount,
    long EdgeCount,
    long NexusCount,
    long DuplicateEntityCount,
    long SemanticMatchCount);

/// <summary>公開仕様のgraph JSON v1をcaller所有のwrite transactionへ逐次importする。</summary>
public static class GraphJsonImporter
{
    /// <summary>
    /// 単一のUTF-8 JSON文書をimportする。transactionとstreamの所有権はcallerに残る。
    /// </summary>
    public static GraphJsonImportResult Import(
        IWriteTransaction transaction,
        Stream document,
        GraphJsonImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Import(transaction, [document], options, cancellationToken);
    }

    /// <summary>
    /// 一つ以上のUTF-8 JSON文書を同じunion operationとしてimportする。
    /// 同一source identityは一つのtarget entityへ対応し、transactionとstreamの所有権はcallerに残る。
    /// </summary>
    public static GraphJsonImportResult Import(
        IWriteTransaction transaction,
        IEnumerable<Stream> documents,
        GraphJsonImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(documents);
        var operation = new GraphJsonImportOperation(
            transaction,
            options ?? new GraphJsonImportOptions(),
            cancellationToken);
        return operation.Import(documents);
    }
}

internal sealed partial class GraphJsonImportOperation
{
    private const long MaxPackedId = (1L << EntityRef.KindShift) - 1;

    private readonly IWriteTransaction _transaction;
    private readonly GraphJsonImportOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<GraphJsonSourceKey, long> _targetIds = [];
    private readonly Dictionary<GraphJsonSourceKey, GraphJsonDefinitionFingerprint> _definitions = [];
    private readonly Dictionary<string, PropertyCardinality> _propertyCardinalities =
        new(StringComparer.Ordinal);
    private int _documentIndex;
    private long _verticesCreated;
    private long _edgesCreated;
    private long _nexusesCreated;
    private long _duplicates;

    internal GraphJsonImportOperation(
        IWriteTransaction transaction,
        GraphJsonImportOptions options,
        CancellationToken cancellationToken)
    {
        _transaction = transaction;
        _options = options;
        _cancellationToken = cancellationToken;
    }

    internal GraphJsonImportResult Import(IEnumerable<Stream> documents)
    {
        int count = 0;
        foreach (Stream? document in documents)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (document is null)
                throw new ArgumentException("入力文書列にnull streamが含まれています。", nameof(documents));
            if (!document.CanRead)
                throw new ArgumentException($"入力文書 {count} は読み取りできません。", nameof(documents));
            _documentIndex = count;
            ImportDocument(document);
            count++;
        }

        if (count == 0)
            throw new ArgumentException("一つ以上の入力文書が必要です。", nameof(documents));

        CompleteSemanticMerge();

        return new GraphJsonImportResult(
            count,
            _verticesCreated,
            _edgesCreated,
            _nexusesCreated,
            _duplicates,
            _semanticMatches);
    }

    private void ImportDocument(Stream document)
    {
        var reader = new GraphJsonStreamingReader(document, _documentIndex, _cancellationToken);
        Expect(reader, JsonTokenType.StartObject, "top-level JSON objectが必要です。");
        var members = new HashSet<string>(StringComparer.Ordinal);
        string? format = null;
        int? version = null;
        GraphJsonSourceDefinition? source = null;
        GraphJsonSchemaDefinition? schema = null;
        HashSet<long>? documentVertices = null;
        bool verticesRead = false;
        bool edgesRead = false;
        bool nexusesRead = false;

        while (Read(reader, "top-level objectが途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndObject)
                break;
            string member = ReadMemberName(reader, members, "top-level object");
            switch (member)
            {
                case "format":
                    format = ReadString(reader, "formatは文字列でなければなりません。");
                    if (format != "quiver-graph")
                        throw Error(reader, $"未対応のformat '{format}' です。");
                    break;
                case "version":
                    version = ReadInt32Number(reader, "versionは整数でなければなりません。");
                    if (version != 1)
                        throw Error(reader, $"未対応のgraph JSON version {version} です。");
                    break;
                case "source":
                    source = ReadSource(reader);
                    break;
                case "schema":
                    schema = ReadSchema(reader);
                    ApplySchema(schema, reader);
                    break;
                case "vertices":
                    RequireHeader(source, schema, reader);
                    if (edgesRead || nexusesRead)
                        throw Error(reader, "vertices配列はrelation配列より前に置く必要があります。");
                    documentVertices = ReadVertices(reader, source!.Value.DatabaseId, schema!);
                    verticesRead = true;
                    break;
                case "edges":
                    RequireEntityPrelude(source, schema, verticesRead, reader);
                    ReadEdges(reader, source!.Value.DatabaseId, schema!, documentVertices!);
                    edgesRead = true;
                    break;
                case "nexuses":
                    RequireEntityPrelude(source, schema, verticesRead, reader);
                    ReadNexuses(reader, source!.Value.DatabaseId, schema!, documentVertices!);
                    nexusesRead = true;
                    break;
                default:
                    throw Error(reader, $"未知のtop-level member '{member}' です。");
            }
        }

        if (format is null || version is null || source is null || schema is null
            || !verticesRead || !edgesRead || !nexusesRead)
        {
            throw Error(reader,
                "format、version、source、schema、vertices、edges、nexusesはすべて必須です。");
        }

        if (reader.Read())
            throw Error(reader, "top-level objectの後に余分なJSON tokenがあります。");
    }

    private static void RequireHeader(
        GraphJsonSourceDefinition? source,
        GraphJsonSchemaDefinition? schema,
        GraphJsonStreamingReader reader)
    {
        if (source is null || schema is null)
            throw Error(reader, "sourceとschemaはentity配列より前に置く必要があります。");
    }

    private static void RequireEntityPrelude(
        GraphJsonSourceDefinition? source,
        GraphJsonSchemaDefinition? schema,
        bool verticesRead,
        GraphJsonStreamingReader reader)
    {
        RequireHeader(source, schema, reader);
        if (!verticesRead)
            throw Error(reader, "vertices配列はedgesとnexusesより前に置く必要があります。");
    }

    private GraphJsonSourceDefinition ReadSource(GraphJsonStreamingReader reader)
    {
        Expect(reader, JsonTokenType.StartObject, "sourceはobjectでなければなりません。");
        var members = new HashSet<string>(StringComparer.Ordinal);
        DatabaseInstanceId? databaseId = null;
        Guid? snapshot = null;
        while (Read(reader, "source objectが途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndObject)
                break;
            string member = ReadMemberName(reader, members, "source object");
            switch (member)
            {
                case "databaseId":
                {
                    string text = ReadString(reader, "source.databaseIdはUUID文字列でなければなりません。");
                    if (!Guid.TryParseExact(text, "D", out Guid value) || value == Guid.Empty)
                        throw Error(reader, "source.databaseIdは空でない標準UUID文字列でなければなりません。");
                    databaseId = new DatabaseInstanceId(value);
                    break;
                }
                case "snapshot":
                {
                    string text = ReadString(reader, "source.snapshotはUUID文字列でなければなりません。");
                    if (!Guid.TryParseExact(text, "D", out Guid value) || value == Guid.Empty)
                        throw Error(reader, "source.snapshotは空でない標準UUID文字列でなければなりません。");
                    snapshot = value;
                    break;
                }
                default:
                    throw Error(reader, $"未知のsource member '{member}' です。");
            }
        }

        if (databaseId is null || snapshot is null)
            throw Error(reader, "source.databaseIdとsource.snapshotは必須です。");
        return new GraphJsonSourceDefinition(databaseId.Value, snapshot.Value);
    }

    private GraphJsonSchemaDefinition ReadSchema(GraphJsonStreamingReader reader)
    {
        Expect(reader, JsonTokenType.StartObject, "schemaはobjectでなければなりません。");
        var members = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? labels = null;
        HashSet<string>? edgeTypes = null;
        HashSet<string>? nexusTypes = null;
        HashSet<string>? roles = null;
        Dictionary<string, PropertyCardinality>? propertyKeys = null;
        while (Read(reader, "schema objectが途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndObject)
                break;
            string member = ReadMemberName(reader, members, "schema object");
            switch (member)
            {
                case "labels":
                    labels = ReadUniqueStringArray(reader, "schema.labels");
                    break;
                case "edgeTypes":
                    edgeTypes = ReadUniqueStringArray(reader, "schema.edgeTypes");
                    break;
                case "nexusTypes":
                    nexusTypes = ReadUniqueStringArray(reader, "schema.nexusTypes");
                    break;
                case "roles":
                    roles = ReadUniqueStringArray(reader, "schema.roles");
                    break;
                case "propertyKeys":
                    propertyKeys = ReadPropertyKeySchema(reader);
                    break;
                default:
                    throw Error(reader, $"未知のschema member '{member}' です。");
            }
        }

        if (labels is null || edgeTypes is null || nexusTypes is null
            || roles is null || propertyKeys is null)
        {
            throw Error(reader,
                "schema.labels、edgeTypes、nexusTypes、roles、propertyKeysはすべて必須です。");
        }
        return new GraphJsonSchemaDefinition(labels, edgeTypes, nexusTypes, roles, propertyKeys);
    }

    private Dictionary<string, PropertyCardinality> ReadPropertyKeySchema(
        GraphJsonStreamingReader reader)
    {
        Expect(reader, JsonTokenType.StartArray, "schema.propertyKeysはarrayでなければなりません。");
        var result = new Dictionary<string, PropertyCardinality>(StringComparer.Ordinal);
        while (Read(reader, "schema.propertyKeys配列が途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndArray)
                return result;
            if (reader.Current.Type != JsonTokenType.StartObject)
                throw Error(reader, "schema.propertyKeysの要素はobjectでなければなりません。");
            var members = new HashSet<string>(StringComparer.Ordinal);
            string? name = null;
            PropertyCardinality? cardinality = null;
            while (Read(reader, "property key schemaが途中で終了しました。"))
            {
                if (reader.Current.Type == JsonTokenType.EndObject)
                    break;
                string member = ReadMemberName(reader, members, "property key schema");
                switch (member)
                {
                    case "name":
                        name = ReadString(reader, "property key nameは文字列でなければなりません。");
                        break;
                    case "cardinality":
                        cardinality = ReadCardinality(reader);
                        break;
                    default:
                        throw Error(reader, $"未知のproperty key schema member '{member}' です。");
                }
            }
            if (name is null || cardinality is null)
                throw Error(reader, "property key schemaにはnameとcardinalityが必要です。");
            if (!result.TryAdd(name, cardinality.Value))
                throw Error(reader, $"schema.propertyKeysに'{name}'が重複しています。");
        }
        throw Error(reader, "schema.propertyKeys配列が途中で終了しました。");
    }

    private void ApplySchema(
        GraphJsonSchemaDefinition schema,
        GraphJsonStreamingReader reader)
    {
        try
        {
            foreach (string label in schema.Labels)
                _transaction.EditSchema.GetOrCreateLabel(label);
            foreach (string edgeType in schema.EdgeTypes)
                _transaction.EditSchema.GetOrCreateEdgeType(edgeType);
            foreach (string nexusType in schema.NexusTypes)
                _transaction.EditSchema.GetOrCreateNexusType(nexusType);
            foreach (string role in schema.Roles)
                _transaction.EditSchema.EnsureRole(role);
            foreach ((string key, PropertyCardinality cardinality) in schema.PropertyKeys)
            {
                if (_propertyCardinalities.TryGetValue(key, out PropertyCardinality existing)
                    && existing != cardinality)
                {
                    throw Error(reader,
                        $"property key '{key}' のcardinalityが文書間で競合しています。" );
                }
                _propertyCardinalities[key] = cardinality;
                _transaction.EditSchema.GetOrCreatePropertyKey(key, cardinality);
            }
        }
        catch (GraphJsonImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new GraphJsonImportException(
                $"schemaを適用できません: {exception.Message}",
                _documentIndex,
                reader.Current.ByteOffset,
                exception);
        }
    }

    private static HashSet<string> ReadUniqueStringArray(
        GraphJsonStreamingReader reader,
        string path)
    {
        Expect(reader, JsonTokenType.StartArray, $"{path}はarrayでなければなりません。");
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (Read(reader, $"{path}配列が途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndArray)
                return result;
            if (reader.Current.Type != JsonTokenType.String)
                throw Error(reader, $"{path}の要素は文字列でなければなりません。");
            string value = reader.Current.Text!;
            if (!result.Add(value))
                throw Error(reader, $"{path}に'{value}'が重複しています。");
        }
        throw Error(reader, $"{path}配列が途中で終了しました。");
    }

    private HashSet<long> ReadVertices(
        GraphJsonStreamingReader reader,
        DatabaseInstanceId databaseId,
        GraphJsonSchemaDefinition schema)
    {
        Expect(reader, JsonTokenType.StartArray, "verticesはarrayでなければなりません。");
        var documentIds = new HashSet<long>();
        while (Read(reader, "vertices配列が途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndArray)
                return documentIds;
            if (reader.Current.Type != JsonTokenType.StartObject)
                throw Error(reader, "verticesの要素はobjectでなければなりません。");
            GraphJsonVertexDefinition vertex = ReadVertex(reader, schema);
            if (!documentIds.Add(vertex.Id))
                throw Error(reader, $"verticesにsource ID {vertex.Id}が重複しています。");

            var key = new GraphJsonSourceKey(databaseId, EntityKind.Vertex, vertex.Id);
            GraphJsonDefinitionFingerprint fingerprint = GraphJsonDefinitionHasher.Vertex(vertex);
            if (TryUseDuplicate(key, fingerprint, reader))
                continue;

            try
            {
                (VertexId target, bool created) = SemanticMergeEnabled
                    ? ResolveSemanticVertex(key, vertex, reader)
                    : (_transaction.CreateVertex(vertex.Label), true);
                if (SemanticMergeEnabled)
                    DeferSemanticProperties(key, target.Value, vertex.Properties, IdentityKey(vertex.Label));
                else
                    ApplyProperties(EntityKind.Vertex, target.Value, vertex.Properties);
                Register(key, fingerprint, target.Value);
                if (created)
                    _verticesCreated++;
            }
            catch (GraphJsonImportException)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw ApplyError(reader, $"Vertex {vertex.Id}を作成できません", exception);
            }
        }
        throw Error(reader, "vertices配列が途中で終了しました。");
    }

    private void ReadEdges(
        GraphJsonStreamingReader reader,
        DatabaseInstanceId databaseId,
        GraphJsonSchemaDefinition schema,
        IReadOnlySet<long> documentVertices)
    {
        Expect(reader, JsonTokenType.StartArray, "edgesはarrayでなければなりません。");
        var documentIds = new HashSet<long>();
        while (Read(reader, "edges配列が途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndArray)
                return;
            if (reader.Current.Type != JsonTokenType.StartObject)
                throw Error(reader, "edgesの要素はobjectでなければなりません。");
            GraphJsonEdgeDefinition edge = ReadEdge(reader, schema);
            if (!documentIds.Add(edge.Id))
                throw Error(reader, $"edgesにsource ID {edge.Id}が重複しています。");
            EnsureDocumentVertex(documentVertices, edge.Source, "Edge source", reader);
            EnsureDocumentVertex(documentVertices, edge.Target, "Edge target", reader);

            var key = new GraphJsonSourceKey(databaseId, EntityKind.Edge, edge.Id);
            GraphJsonDefinitionFingerprint fingerprint = GraphJsonDefinitionHasher.Edge(edge);
            if (TryUseDuplicate(key, fingerprint, reader))
                continue;

            VertexId source = ResolveVertex(databaseId, edge.Source, reader);
            VertexId target = ResolveVertex(databaseId, edge.Target, reader);
            try
            {
                (EdgeId targetEdge, bool created) = SemanticMergeEnabled
                    ? ResolveSemanticEdge(source, target, edge.Type, reader)
                    : (_transaction.CreateEdge(source, target, edge.Type), true);
                if (SemanticMergeEnabled)
                    DeferSemanticProperties(key, targetEdge.Value, edge.Properties, identityKey: null);
                else
                    ApplyProperties(EntityKind.Edge, targetEdge.Value, edge.Properties);
                Register(key, fingerprint, targetEdge.Value);
                if (created)
                    _edgesCreated++;
            }
            catch (GraphJsonImportException)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw ApplyError(reader, $"Edge {edge.Id}を作成できません", exception);
            }
        }
        throw Error(reader, "edges配列が途中で終了しました。");
    }

    private void ReadNexuses(
        GraphJsonStreamingReader reader,
        DatabaseInstanceId databaseId,
        GraphJsonSchemaDefinition schema,
        IReadOnlySet<long> documentVertices)
    {
        Expect(reader, JsonTokenType.StartArray, "nexusesはarrayでなければなりません。");
        var documentIds = new HashSet<long>();
        while (Read(reader, "nexuses配列が途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndArray)
                return;
            if (reader.Current.Type != JsonTokenType.StartObject)
                throw Error(reader, "nexusesの要素はobjectでなければなりません。");
            GraphJsonNexusDefinition nexus = ReadNexus(reader, schema);
            if (!documentIds.Add(nexus.Id))
                throw Error(reader, $"nexusesにsource ID {nexus.Id}が重複しています。");
            foreach (GraphJsonImportMember member in nexus.Members)
                EnsureDocumentVertex(documentVertices, member.VertexId, "Nexus member", reader);

            var key = new GraphJsonSourceKey(databaseId, EntityKind.Nexus, nexus.Id);
            GraphJsonDefinitionFingerprint fingerprint = GraphJsonDefinitionHasher.Nexus(nexus);
            if (TryUseDuplicate(key, fingerprint, reader))
                continue;

            NexusMember[] remapped = nexus.Members
                .Select(member => new NexusMember(
                    member.Role,
                    ResolveVertex(databaseId, member.VertexId, reader)))
                .ToArray();
            try
            {
                (NexusId targetNexus, bool created) = SemanticMergeEnabled
                    ? ResolveSemanticNexus(nexus.Type, remapped, reader)
                    : (_transaction.CreateNexus(nexus.Type, remapped), true);
                if (SemanticMergeEnabled)
                    DeferSemanticProperties(key, targetNexus.Value, nexus.Properties, identityKey: null);
                else
                    ApplyProperties(EntityKind.Nexus, targetNexus.Value, nexus.Properties);
                Register(key, fingerprint, targetNexus.Value);
                if (created)
                    _nexusesCreated++;
            }
            catch (GraphJsonImportException)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw ApplyError(reader, $"Nexus {nexus.Id}を作成できません", exception);
            }
        }
        throw Error(reader, "nexuses配列が途中で終了しました。");
    }

    private GraphJsonVertexDefinition ReadVertex(
        GraphJsonStreamingReader reader,
        GraphJsonSchemaDefinition schema)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        long? id = null;
        string? label = null;
        List<GraphJsonImportProperty>? properties = null;
        while (Read(reader, "Vertex objectが途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndObject)
                break;
            string member = ReadMemberName(reader, members, "Vertex object");
            switch (member)
            {
                case "id":
                    id = ReadPackedId(reader, "Vertex id");
                    break;
                case "label":
                    label = ReadString(reader, "Vertex labelは文字列でなければなりません。");
                    break;
                case "properties":
                    properties = ReadProperties(reader, schema);
                    break;
                default:
                    throw Error(reader, $"未知のVertex member '{member}' です。");
            }
        }
        if (id is null || label is null || properties is null)
            throw Error(reader, "Vertexにはid、label、propertiesが必要です。");
        if (!schema.Labels.Contains(label))
            throw Error(reader, $"Vertex label '{label}' がschema.labelsにありません。");
        return new GraphJsonVertexDefinition(id.Value, label, properties);
    }

    private GraphJsonEdgeDefinition ReadEdge(
        GraphJsonStreamingReader reader,
        GraphJsonSchemaDefinition schema)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);
        long? id = null;
        long? source = null;
        long? target = null;
        string? type = null;
        List<GraphJsonImportProperty>? properties = null;
        while (Read(reader, "Edge objectが途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndObject)
                break;
            string member = ReadMemberName(reader, members, "Edge object");
            switch (member)
            {
                case "id":
                    id = ReadPackedId(reader, "Edge id");
                    break;
                case "source":
                    source = ReadPackedId(reader, "Edge source");
                    break;
                case "target":
                    target = ReadPackedId(reader, "Edge target");
                    break;
                case "type":
                    type = ReadString(reader, "Edge typeは文字列でなければなりません。");
                    break;
                case "properties":
                    properties = ReadProperties(reader, schema);
                    break;
                default:
                    throw Error(reader, $"未知のEdge member '{member}' です。");
            }
        }
        if (id is null || source is null || target is null || type is null || properties is null)
            throw Error(reader, "Edgeにはid、source、target、type、propertiesが必要です。");
        if (!schema.EdgeTypes.Contains(type))
            throw Error(reader, $"Edge type '{type}' がschema.edgeTypesにありません。");
        return new GraphJsonEdgeDefinition(id.Value, type, source.Value, target.Value, properties);
    }

    private GraphJsonNexusDefinition ReadNexus(
        GraphJsonStreamingReader reader,
        GraphJsonSchemaDefinition schema)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        long? id = null;
        string? type = null;
        List<GraphJsonImportMember>? members = null;
        List<GraphJsonImportProperty>? properties = null;
        while (Read(reader, "Nexus objectが途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndObject)
                break;
            string field = ReadMemberName(reader, fields, "Nexus object");
            switch (field)
            {
                case "id":
                    id = ReadPackedId(reader, "Nexus id");
                    break;
                case "type":
                    type = ReadString(reader, "Nexus typeは文字列でなければなりません。");
                    break;
                case "members":
                    members = ReadMembers(reader, schema);
                    break;
                case "properties":
                    properties = ReadProperties(reader, schema);
                    break;
                default:
                    throw Error(reader, $"未知のNexus member '{field}' です。");
            }
        }
        if (id is null || type is null || members is null || properties is null)
            throw Error(reader, "Nexusにはid、type、members、propertiesが必要です。");
        if (!schema.NexusTypes.Contains(type))
            throw Error(reader, $"Nexus type '{type}' がschema.nexusTypesにありません。");
        if (members.Count < 2)
            throw Error(reader, "Nexusには2件以上のmemberが必要です。");
        members.Sort(GraphJsonImportMemberComparer.Instance);
        for (int i = 1; i < members.Count; i++)
        {
            if (GraphJsonImportMemberComparer.Instance.Compare(members[i - 1], members[i]) == 0)
                throw Error(reader,
                    $"Nexusに重複member ({members[i].Role}, {members[i].VertexId}) があります。");
        }
        return new GraphJsonNexusDefinition(id.Value, type, members, properties);
    }

    private List<GraphJsonImportMember> ReadMembers(
        GraphJsonStreamingReader reader,
        GraphJsonSchemaDefinition schema)
    {
        Expect(reader, JsonTokenType.StartArray, "Nexus membersはarrayでなければなりません。");
        var result = new List<GraphJsonImportMember>();
        while (Read(reader, "Nexus members配列が途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndArray)
                return result;
            if (reader.Current.Type != JsonTokenType.StartObject)
                throw Error(reader, "Nexus memberはobjectでなければなりません。");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            string? role = null;
            long? vertex = null;
            while (Read(reader, "Nexus member objectが途中で終了しました。"))
            {
                if (reader.Current.Type == JsonTokenType.EndObject)
                    break;
                string field = ReadMemberName(reader, fields, "Nexus member object");
                switch (field)
                {
                    case "role":
                        role = ReadString(reader, "Nexus member roleは文字列でなければなりません。");
                        break;
                    case "vertex":
                        vertex = ReadPackedId(reader, "Nexus member vertex");
                        break;
                    default:
                        throw Error(reader, $"未知のNexus member field '{field}' です。");
                }
            }
            if (role is null || vertex is null)
                throw Error(reader, "Nexus memberにはroleとvertexが必要です。");
            if (!schema.Roles.Contains(role))
                throw Error(reader, $"Nexus role '{role}' がschema.rolesにありません。");
            result.Add(new GraphJsonImportMember(role, vertex.Value));
        }
        throw Error(reader, "Nexus members配列が途中で終了しました。");
    }

    private List<GraphJsonImportProperty> ReadProperties(
        GraphJsonStreamingReader reader,
        GraphJsonSchemaDefinition schema)
    {
        Expect(reader, JsonTokenType.StartArray, "propertiesはarrayでなければなりません。");
        var result = new List<GraphJsonImportProperty>();
        while (Read(reader, "properties配列が途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndArray)
            {
                ValidateAndNormalizeProperties(result, schema, reader);
                return result;
            }
            if (reader.Current.Type != JsonTokenType.StartObject)
                throw Error(reader, "propertyはobjectでなければなりません。");
            result.Add(ReadProperty(reader));
        }
        throw Error(reader, "properties配列が途中で終了しました。");
    }

    private GraphJsonImportProperty ReadProperty(GraphJsonStreamingReader reader)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        string? key = null;
        PropertyCardinality? cardinality = null;
        PropertyValueType? type = null;
        GraphJsonRawValue? rawValue = null;
        while (Read(reader, "property objectが途中で終了しました。"))
        {
            if (reader.Current.Type == JsonTokenType.EndObject)
                break;
            string field = ReadMemberName(reader, fields, "property object");
            switch (field)
            {
                case "key":
                    key = ReadString(reader, "property keyは文字列でなければなりません。");
                    break;
                case "cardinality":
                    cardinality = ReadCardinality(reader);
                    break;
                case "type":
                    type = ReadPropertyType(reader);
                    break;
                case "value":
                    rawValue = ReadRawValue(reader);
                    break;
                default:
                    throw Error(reader, $"未知のproperty member '{field}' です。");
            }
        }
        if (key is null || cardinality is null || type is null || rawValue is null)
            throw Error(reader, "propertyにはkey、cardinality、type、valueが必要です。");
        GraphJsonImportValue value = DecodeValue(type.Value, rawValue, reader);
        return new GraphJsonImportProperty(key, cardinality.Value, value);
    }

    private static GraphJsonRawValue ReadRawValue(GraphJsonStreamingReader reader)
    {
        if (!Read(reader, "property valueがありません。"))
            throw Error(reader, "property valueがありません。");
        return reader.Current.Type switch
        {
            JsonTokenType.True => GraphJsonRawValue.Boolean(true),
            JsonTokenType.False => GraphJsonRawValue.Boolean(false),
            JsonTokenType.String => GraphJsonRawValue.String(reader.Current.Text!),
            JsonTokenType.Number => GraphJsonRawValue.Number(reader.Current.Text!),
            JsonTokenType.StartArray => ReadRawArray(reader),
            _ => throw Error(reader, "property valueのJSON形が未対応です。"),
        };
    }

    private static GraphJsonRawValue ReadRawArray(GraphJsonStreamingReader reader)
    {
        var values = new List<GraphJsonRawValue>();
        while (Read(reader, "property value arrayが途中で終了しました。"))
        {
            switch (reader.Current.Type)
            {
                case JsonTokenType.EndArray:
                    return GraphJsonRawValue.Array(values);
                case JsonTokenType.String:
                    values.Add(GraphJsonRawValue.String(reader.Current.Text!));
                    break;
                case JsonTokenType.Number:
                    values.Add(GraphJsonRawValue.Number(reader.Current.Text!));
                    break;
                default:
                    throw Error(reader, "FloatArrayの要素はnumberまたは予約文字列でなければなりません。");
            }
        }
        throw Error(reader, "property value arrayが途中で終了しました。");
    }

    private static GraphJsonImportValue DecodeValue(
        PropertyValueType type,
        GraphJsonRawValue raw,
        GraphJsonStreamingReader reader)
    {
        switch (type)
        {
            case PropertyValueType.Bool:
                if (raw.Kind != GraphJsonRawValueKind.Boolean)
                    throw Error(reader, "bool property valueはtrueまたはfalseでなければなりません。");
                return GraphJsonImportValue.Bool((bool)raw.Value);
            case PropertyValueType.Int32:
                if (raw.Kind != GraphJsonRawValueKind.Number
                    || !int.TryParse(
                        (string)raw.Value,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out int int32))
                {
                    throw Error(reader, "int32 property valueは範囲内のJSON整数でなければなりません。");
                }
                return GraphJsonImportValue.Int32(int32);
            case PropertyValueType.Int64:
                if (raw.Kind != GraphJsonRawValueKind.String
                    || !IsDecimalInteger((string)raw.Value)
                    || !long.TryParse(
                        (string)raw.Value,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out long int64))
                {
                    throw Error(reader, "int64 property valueは範囲内の10進文字列でなければなりません。");
                }
                return GraphJsonImportValue.Int64(int64);
            case PropertyValueType.Double:
                return GraphJsonImportValue.Double(ReadDouble(raw, reader));
            case PropertyValueType.String:
                if (raw.Kind != GraphJsonRawValueKind.String)
                    throw Error(reader, "string property valueはJSON文字列でなければなりません。");
                return GraphJsonImportValue.String((string)raw.Value);
            case PropertyValueType.Bytes:
                if (raw.Kind != GraphJsonRawValueKind.String)
                    throw Error(reader, "bytes property valueはBase64文字列でなければなりません。");
                try
                {
                    string base64 = (string)raw.Value;
                    byte[] bytes = Convert.FromBase64String(base64);
                    if (!StringComparer.Ordinal.Equals(Convert.ToBase64String(bytes), base64))
                        throw Error(reader, "bytes property valueは正準Base64文字列でなければなりません。");
                    return GraphJsonImportValue.Bytes(bytes);
                }
                catch (FormatException exception)
                {
                    throw new GraphJsonImportException(
                        "bytes property valueのBase64が不正です。",
                        reader.DocumentIndex,
                        reader.Current.ByteOffset,
                        exception);
                }
            case PropertyValueType.FloatArray:
                if (raw.Kind != GraphJsonRawValueKind.Array)
                    throw Error(reader, "floatArray property valueはarrayでなければなりません。");
                var elements = (IReadOnlyList<GraphJsonRawValue>)raw.Value;
                var floats = new float[elements.Count];
                for (int i = 0; i < elements.Count; i++)
                    floats[i] = ReadSingle(elements[i], reader);
                return GraphJsonImportValue.FloatArray(floats);
            default:
                throw Error(reader, $"未対応のproperty type {type} です。");
        }
    }

    private static double ReadDouble(
        GraphJsonRawValue raw,
        GraphJsonStreamingReader reader)
    {
        if (raw.Kind == GraphJsonRawValueKind.String)
        {
            return (string)raw.Value switch
            {
                "NaN" => double.NaN,
                "Infinity" => double.PositiveInfinity,
                "-Infinity" => double.NegativeInfinity,
                string reserved => throw Error(reader,
                    $"未知のdouble予約文字列 '{reserved}' です。"),
            };
        }
        if (raw.Kind != GraphJsonRawValueKind.Number
            || !double.TryParse(
                (string)raw.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value)
            || !double.IsFinite(value))
        {
            throw Error(reader, "double property valueは有限JSON numberまたは予約文字列でなければなりません。");
        }
        return value;
    }

    private static float ReadSingle(
        GraphJsonRawValue raw,
        GraphJsonStreamingReader reader)
    {
        if (raw.Kind == GraphJsonRawValueKind.String)
        {
            return (string)raw.Value switch
            {
                "NaN" => float.NaN,
                "Infinity" => float.PositiveInfinity,
                "-Infinity" => float.NegativeInfinity,
                string reserved => throw Error(reader,
                    $"未知のfloatArray予約文字列 '{reserved}' です。"),
            };
        }
        if (raw.Kind != GraphJsonRawValueKind.Number
            || !float.TryParse(
                (string)raw.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float value)
            || !float.IsFinite(value))
        {
            throw Error(reader, "floatArray要素は有限JSON numberまたは予約文字列でなければなりません。");
        }
        return value;
    }

    private static bool IsDecimalInteger(string value)
    {
        if (value.Length == 0)
            return false;
        int index = value[0] == '-' ? 1 : 0;
        if (index == value.Length)
            return false;
        for (; index < value.Length; index++)
        {
            if (value[index] is < '0' or > '9')
                return false;
        }
        return true;
    }

    private static void ValidateAndNormalizeProperties(
        List<GraphJsonImportProperty> properties,
        GraphJsonSchemaDefinition schema,
        GraphJsonStreamingReader reader)
    {
        var byKey = properties.GroupBy(property => property.Key, StringComparer.Ordinal);
        foreach (IGrouping<string, GraphJsonImportProperty> group in byKey)
        {
            if (!schema.PropertyKeys.TryGetValue(group.Key, out PropertyCardinality schemaCardinality))
                throw Error(reader, $"property key '{group.Key}' がschema.propertyKeysにありません。");
            GraphJsonImportProperty[] values = group.ToArray();
            if (values.Any(value => value.Cardinality != schemaCardinality))
                throw Error(reader, $"property key '{group.Key}' のcardinalityがschemaと一致しません。");
            if (schemaCardinality == PropertyCardinality.Single && values.Length != 1)
                throw Error(reader, $"Single property '{group.Key}' が複数定義されています。");
            if (schemaCardinality == PropertyCardinality.Set)
            {
                Array.Sort(values, GraphJsonImportPropertyComparer.Instance);
                for (int i = 1; i < values.Length; i++)
                {
                    if (GraphJsonImportPropertyComparer.Instance.Compare(values[i - 1], values[i]) == 0)
                        throw Error(reader, $"Set property '{group.Key}' に同じ値が重複しています。");
                }
            }
        }
        properties.Sort(GraphJsonImportPropertyComparer.Instance);
    }

    private bool TryUseDuplicate(
        GraphJsonSourceKey key,
        GraphJsonDefinitionFingerprint fingerprint,
        GraphJsonStreamingReader reader)
    {
        if (!_definitions.TryGetValue(key, out GraphJsonDefinitionFingerprint existing))
            return false;
        if (existing != fingerprint)
        {
            throw Error(reader,
                $"source identity ({key.DatabaseId}, {key.Kind}, {key.PackedId}) の定義が文書間で競合しています。");
        }
        _duplicates++;
        return true;
    }

    private void Register(
        GraphJsonSourceKey key,
        GraphJsonDefinitionFingerprint fingerprint,
        long targetPackedId)
    {
        _definitions.Add(key, fingerprint);
        _targetIds.Add(key, targetPackedId);
        _options.MappingSink?.Invoke(new GraphJsonImportMapping(
            key.DatabaseId,
            key.Kind,
            key.PackedId,
            targetPackedId));
    }

    private VertexId ResolveVertex(
        DatabaseInstanceId databaseId,
        long sourcePackedId,
        GraphJsonStreamingReader reader)
    {
        var key = new GraphJsonSourceKey(databaseId, EntityKind.Vertex, sourcePackedId);
        if (!_targetIds.TryGetValue(key, out long targetPackedId))
            throw Error(reader, $"参照先Vertex {sourcePackedId} のmappingがありません。");
        return new VertexId(targetPackedId);
    }

    private static void EnsureDocumentVertex(
        IReadOnlySet<long> documentVertices,
        long sourcePackedId,
        string relationPart,
        GraphJsonStreamingReader reader)
    {
        if (!documentVertices.Contains(sourcePackedId))
        {
            throw Error(reader,
                $"{relationPart} {sourcePackedId} は同じ文書のverticesに定義されていません。");
        }
    }

    private void ApplyProperties(
        EntityKind kind,
        long targetPackedId,
        IReadOnlyList<GraphJsonImportProperty> properties)
    {
        foreach (GraphJsonImportProperty property in properties)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (property.Cardinality == PropertyCardinality.Single
                && property.Value.Type == PropertyValueType.FloatArray)
            {
                _transaction.SetVectorProperty(
                    ToEntityRef(kind, targetPackedId),
                    property.Key,
                    (float[])property.Value.Value);
                continue;
            }

            PropertyValue value = property.Value.AsPropertyValue();
            if (property.Cardinality == PropertyCardinality.Single)
                SetProperty(kind, targetPackedId, property.Key, in value);
            else
                AddPropertyValue(kind, targetPackedId, property.Key, in value);
        }
    }

    private void SetProperty(
        EntityKind kind,
        long targetPackedId,
        string key,
        in PropertyValue value)
    {
        switch (kind)
        {
            case EntityKind.Vertex:
                _transaction.SetProperty(new VertexId(targetPackedId), key, in value);
                break;
            case EntityKind.Edge:
                _transaction.SetProperty(new EdgeId(targetPackedId), key, in value);
                break;
            case EntityKind.Nexus:
                _transaction.SetProperty(new NexusId(targetPackedId), key, in value);
                break;
            default:
                throw new InvalidOperationException($"未対応のentity kind {kind} です。");
        }
    }

    private void AddPropertyValue(
        EntityKind kind,
        long targetPackedId,
        string key,
        in PropertyValue value)
    {
        switch (kind)
        {
            case EntityKind.Vertex:
                _transaction.AddPropertyValue(new VertexId(targetPackedId), key, in value);
                break;
            case EntityKind.Edge:
                _transaction.AddPropertyValue(new EdgeId(targetPackedId), key, in value);
                break;
            case EntityKind.Nexus:
                _transaction.AddPropertyValue(new NexusId(targetPackedId), key, in value);
                break;
            default:
                throw new InvalidOperationException($"未対応のentity kind {kind} です。");
        }
    }

    private static EntityRef ToEntityRef(EntityKind kind, long packedId) => kind switch
    {
        EntityKind.Vertex => EntityRef.From(new VertexId(packedId)),
        EntityKind.Edge => EntityRef.From(new EdgeId(packedId)),
        EntityKind.Nexus => EntityRef.From(new NexusId(packedId)),
        _ => throw new InvalidOperationException($"未対応のentity kind {kind} です。"),
    };

    private static PropertyCardinality ReadCardinality(GraphJsonStreamingReader reader)
    {
        string value = ReadString(reader, "cardinalityは文字列でなければなりません。");
        return value switch
        {
            "single" => PropertyCardinality.Single,
            "set" => PropertyCardinality.Set,
            _ => throw Error(reader, $"未知のcardinality '{value}' です。"),
        };
    }

    private static PropertyValueType ReadPropertyType(GraphJsonStreamingReader reader)
    {
        string value = ReadString(reader, "property typeは文字列でなければなりません。");
        return value switch
        {
            "bool" => PropertyValueType.Bool,
            "int32" => PropertyValueType.Int32,
            "int64" => PropertyValueType.Int64,
            "double" => PropertyValueType.Double,
            "string" => PropertyValueType.String,
            "bytes" => PropertyValueType.Bytes,
            "floatArray" => PropertyValueType.FloatArray,
            _ => throw Error(reader, $"未知のproperty type '{value}' です。"),
        };
    }

    private static long ReadPackedId(GraphJsonStreamingReader reader, string path)
    {
        string value = ReadString(reader, $"{path}は10進文字列でなければなりません。");
        if (!IsDecimalInteger(value)
            || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long packedId)
            || packedId < 0
            || packedId > MaxPackedId)
        {
            throw Error(reader, $"{path}は有効なgeneration付きpacked IDではありません。");
        }
        return packedId;
    }

    private static int ReadInt32Number(GraphJsonStreamingReader reader, string message)
    {
        if (!Read(reader, message)
            || reader.Current.Type != JsonTokenType.Number
            || !int.TryParse(
                reader.Current.Text,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out int value))
        {
            throw Error(reader, message);
        }
        return value;
    }

    private static string ReadString(GraphJsonStreamingReader reader, string message)
    {
        if (!Read(reader, message) || reader.Current.Type != JsonTokenType.String)
            throw Error(reader, message);
        return reader.Current.Text!;
    }

    private static string ReadMemberName(
        GraphJsonStreamingReader reader,
        HashSet<string> members,
        string objectName)
    {
        if (reader.Current.Type != JsonTokenType.PropertyName)
            throw Error(reader, $"{objectName}にはmember nameが必要です。");
        string member = reader.Current.Text!;
        if (!members.Add(member))
            throw Error(reader, $"{objectName}にmember '{member}' が重複しています。");
        return member;
    }

    private static void Expect(
        GraphJsonStreamingReader reader,
        JsonTokenType expected,
        string message)
    {
        if (!Read(reader, message) || reader.Current.Type != expected)
            throw Error(reader, message);
    }

    private static bool Read(GraphJsonStreamingReader reader, string message)
    {
        if (reader.Read())
            return true;
        throw Error(reader, message);
    }

    private static GraphJsonImportException Error(
        GraphJsonStreamingReader reader,
        string message)
        => new(message, reader.DocumentIndex, reader.Current.ByteOffset);

    private GraphJsonImportException ApplyError(
        GraphJsonStreamingReader reader,
        string message,
        Exception exception)
        => new(
            $"{message}: {exception.Message}",
            _documentIndex,
            reader.Current.ByteOffset,
            exception);
}

internal readonly record struct GraphJsonSourceDefinition(
    DatabaseInstanceId DatabaseId,
    Guid SnapshotId);

internal sealed record GraphJsonSchemaDefinition(
    HashSet<string> Labels,
    HashSet<string> EdgeTypes,
    HashSet<string> NexusTypes,
    HashSet<string> Roles,
    Dictionary<string, PropertyCardinality> PropertyKeys);

internal readonly record struct GraphJsonSourceKey(
    DatabaseInstanceId DatabaseId,
    EntityKind Kind,
    long PackedId);

internal sealed record GraphJsonVertexDefinition(
    long Id,
    string Label,
    List<GraphJsonImportProperty> Properties);

internal sealed record GraphJsonEdgeDefinition(
    long Id,
    string Type,
    long Source,
    long Target,
    List<GraphJsonImportProperty> Properties);

internal sealed record GraphJsonNexusDefinition(
    long Id,
    string Type,
    List<GraphJsonImportMember> Members,
    List<GraphJsonImportProperty> Properties);

internal readonly record struct GraphJsonImportMember(string Role, long VertexId);

internal sealed record GraphJsonImportProperty(
    string Key,
    PropertyCardinality Cardinality,
    GraphJsonImportValue Value);

internal sealed record GraphJsonImportValue(
    PropertyValueType Type,
    object Value,
    byte[] CanonicalBytes)
{
    internal static GraphJsonImportValue Bool(bool value)
        => new(PropertyValueType.Bool, value, [value ? (byte)1 : (byte)0]);

    internal static GraphJsonImportValue Int32(int value)
    {
        byte[] canonical = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(canonical, value);
        return new GraphJsonImportValue(PropertyValueType.Int32, value, canonical);
    }

    internal static GraphJsonImportValue Int64(long value)
    {
        byte[] canonical = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(canonical, value);
        return new GraphJsonImportValue(PropertyValueType.Int64, value, canonical);
    }

    internal static GraphJsonImportValue Double(double value)
    {
        byte[] canonical = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(canonical, BitConverter.DoubleToInt64Bits(value));
        return new GraphJsonImportValue(PropertyValueType.Double, value, canonical);
    }

    internal static GraphJsonImportValue String(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        return new GraphJsonImportValue(PropertyValueType.String, utf8, utf8);
    }

    internal static GraphJsonImportValue Bytes(byte[] value)
        => new(PropertyValueType.Bytes, value, value);

    internal static GraphJsonImportValue FloatArray(float[] value)
    {
        byte[] canonical = new byte[checked(value.Length * sizeof(int))];
        for (int i = 0; i < value.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(
                canonical.AsSpan(i * sizeof(int), sizeof(int)),
                BitConverter.SingleToInt32Bits(value[i]));
        }
        return new GraphJsonImportValue(PropertyValueType.FloatArray, value, canonical);
    }

    internal PropertyValue AsPropertyValue() => Type switch
    {
        PropertyValueType.Bool => PropertyValue.FromBool((bool)Value),
        PropertyValueType.Int32 => PropertyValue.FromInt32((int)Value),
        PropertyValueType.Int64 => PropertyValue.FromInt64((long)Value),
        PropertyValueType.Double => PropertyValue.FromDouble((double)Value),
        PropertyValueType.String => PropertyValue.FromUtf8((byte[])Value),
        PropertyValueType.Bytes => PropertyValue.FromBytes((byte[])Value),
        PropertyValueType.FloatArray => PropertyValue.FromFloatArray((float[])Value),
        _ => throw new InvalidOperationException($"未対応のproperty type {Type} です。"),
    };
}

internal enum GraphJsonRawValueKind : byte
{
    Boolean,
    String,
    Number,
    Array,
}

internal sealed record GraphJsonRawValue(GraphJsonRawValueKind Kind, object Value)
{
    internal static GraphJsonRawValue Boolean(bool value)
        => new(GraphJsonRawValueKind.Boolean, value);

    internal static GraphJsonRawValue String(string value)
        => new(GraphJsonRawValueKind.String, value);

    internal static GraphJsonRawValue Number(string value)
        => new(GraphJsonRawValueKind.Number, value);

    internal static GraphJsonRawValue Array(IReadOnlyList<GraphJsonRawValue> value)
        => new(GraphJsonRawValueKind.Array, value);
}

internal sealed class GraphJsonImportPropertyComparer : IComparer<GraphJsonImportProperty>
{
    internal static GraphJsonImportPropertyComparer Instance { get; } = new();

    public int Compare(GraphJsonImportProperty? left, GraphJsonImportProperty? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        int comparison = StringComparer.Ordinal.Compare(left.Key, right.Key);
        if (comparison != 0) return comparison;
        comparison = left.Cardinality.CompareTo(right.Cardinality);
        if (comparison != 0) return comparison;
        comparison = left.Value.Type.CompareTo(right.Value.Type);
        if (comparison != 0) return comparison;
        return CompareBytes(left.Value.CanonicalBytes, right.Value.CanonicalBytes);
    }

    private static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int common = Math.Min(left.Length, right.Length);
        int comparison = left[..common].SequenceCompareTo(right[..common]);
        return comparison != 0 ? comparison : left.Length.CompareTo(right.Length);
    }
}

internal sealed class GraphJsonImportMemberComparer : IComparer<GraphJsonImportMember>
{
    internal static GraphJsonImportMemberComparer Instance { get; } = new();

    public int Compare(GraphJsonImportMember left, GraphJsonImportMember right)
    {
        int comparison = StringComparer.Ordinal.Compare(left.Role, right.Role);
        return comparison != 0 ? comparison : left.VertexId.CompareTo(right.VertexId);
    }
}

internal readonly record struct GraphJsonDefinitionFingerprint(string Value);

internal static class GraphJsonDefinitionHasher
{
    internal static GraphJsonDefinitionFingerprint Vertex(GraphJsonVertexDefinition vertex)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendByte(hash, (byte)EntityKind.Vertex);
        AppendString(hash, vertex.Label);
        AppendProperties(hash, vertex.Properties);
        return Complete(hash);
    }

    internal static GraphJsonDefinitionFingerprint Edge(GraphJsonEdgeDefinition edge)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendByte(hash, (byte)EntityKind.Edge);
        AppendString(hash, edge.Type);
        AppendInt64(hash, edge.Source);
        AppendInt64(hash, edge.Target);
        AppendProperties(hash, edge.Properties);
        return Complete(hash);
    }

    internal static GraphJsonDefinitionFingerprint Nexus(GraphJsonNexusDefinition nexus)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendByte(hash, (byte)EntityKind.Nexus);
        AppendString(hash, nexus.Type);
        AppendInt32(hash, nexus.Members.Count);
        foreach (GraphJsonImportMember member in nexus.Members)
        {
            AppendString(hash, member.Role);
            AppendInt64(hash, member.VertexId);
        }
        AppendProperties(hash, nexus.Properties);
        return Complete(hash);
    }

    private static void AppendProperties(
        IncrementalHash hash,
        IReadOnlyList<GraphJsonImportProperty> properties)
    {
        AppendInt32(hash, properties.Count);
        foreach (GraphJsonImportProperty property in properties)
        {
            AppendString(hash, property.Key);
            AppendByte(hash, (byte)property.Cardinality);
            AppendByte(hash, (byte)property.Value.Type);
            AppendBytes(hash, property.Value.CanonicalBytes);
        }
    }

    private static void AppendString(IncrementalHash hash, string value)
        => AppendBytes(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        AppendInt32(hash, value.Length);
        hash.AppendData(value);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendByte(IncrementalHash hash, byte value)
    {
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = value;
        hash.AppendData(bytes);
    }

    private static GraphJsonDefinitionFingerprint Complete(IncrementalHash hash)
        => new(Convert.ToHexString(hash.GetHashAndReset()));
}
