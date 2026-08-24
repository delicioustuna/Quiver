// schema ツール — DB のグラフスキーマ (ラベル・プロパティ・インデックス) を返す。
// LLM は最初にこのツールを呼び、返却されたスキーマを見て traverse の引数を組み立てる。

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Yatagarasu.Api;
using Yatagarasu.Storage.Records;

namespace Yatagarasu.Mcp;

[McpServerToolType]
internal static class SchemaTool
{
    [McpServerTool(Name = "schema"), Description("Returns the graph schema: vertex labels with properties and indexes, edge types, and vector indexes.")]
    public static string Execute(McpContext ctx)
    {
        var db = ctx.Db;
        var schema = db.Schema;

        var labels = schema.ListLabels();
        var edgeTypes = schema.ListEdgeTypes();
        var allPropKeys = schema.ListPropertyKeys();
        var indexes = schema.ListIndexes();
        var ftIndexes = indexes
            .Select(static index => index.Definition)
            .OfType<FullTextIndexDefinition>()
            .ToList();
        var vecIndexes = indexes
            .Select(static index => index.Definition)
            .OfType<VectorIndexDefinition>()
            .ToList();

        // ラベルごとにプロパティとインデックスを集約する
        var vertexMap = new Dictionary<string, VertexSchemaEntry>();
        foreach (var label in labels)
            vertexMap[label] = new VertexSchemaEntry();

        foreach (var idx in indexes)
        {
            if (idx.Definition is ScalarIndexDefinition &&
                idx.Target.OwnerKind == PropertyOwnerKind.Vertex &&
                idx.Target.Scope is { } label &&
                vertexMap.TryGetValue(label, out var entry))
            {
                entry.Indexes["btree"] = entry.Indexes
                    .GetValueOrDefault("btree", [])
                    .Append(idx.Target.PropertyKey)
                    .ToList();
            }
        }

        foreach (var ftIdx in ftIndexes)
        {
            if (ftIdx.Target.Scope is { } scope
                && vertexMap.TryGetValue(scope, out var entry))
                entry.Indexes["fulltext"] = entry.Indexes
                    .GetValueOrDefault("fulltext", [])
                    .Append(ftIdx.Target.PropertyKey)
                    .ToList();
        }

        // プロパティの名前と型を実データから推定する (下記メソッド参照)
        DiscoverProperties(db, vertexMap, allPropKeys);

        var result = new SchemaResponse
        {
            Vertices = vertexMap.Select(kv => new VertexSchema
            {
                Label = kv.Key,
                Properties = kv.Value.Properties,
                Indexes = kv.Value.Indexes,
            }).ToList(),
            Edges = edgeTypes.Select(rt => new EdgeSchema { Label = rt }).ToList(),
            VectorIndexes = vecIndexes.Select(v => new VectorIndexSchema
            {
                Name = v.Name,
                Dimensions = v.Dimensions,
                Metric = v.Metric.ToString().ToLowerInvariant(),
            }).ToList(),
        };

        return JsonSerializer.Serialize(result, SchemaJsonContext.Default.SchemaResponse);
    }

    // Yatagarasu はスキーマレス設計のため、プロパティの型情報をカタログに持たない。
    // PropertyKeyId → キー名の逆引き API も無いので、
    // ラベルごとに実Vertexをサンプリングし、全プロパティキーに対して HasProperty/GetProperty で
    // 存在するプロパティ名とその型を発見する。
    private static void DiscoverProperties(
        YatagarasuDatabase db, Dictionary<string, VertexSchemaEntry> vertexMap,
        IReadOnlyList<string> allPropKeys)
    {
        const int sampleSize = 50;
        using var tx = db.BeginReadTransaction();
        var g = tx.Query;

        foreach (var (label, entry) in vertexMap)
        {
            var vertices = g.Vertices().HasLabel(label).Limit(sampleSize).ToList();
            foreach (var vertexId in vertices)
            {
                foreach (var key in allPropKeys)
                {
                    if (entry.Properties.ContainsKey(key)) continue;
                    if (!tx.HasProperty(vertexId, key)) continue;
                    var value = tx.GetProperty(vertexId, key);
                    entry.Properties[key] = InferTypeName(value.Type);
                }
            }
        }
    }

    private static string InferTypeName(PropertyValueType type) => type switch
    {
        PropertyValueType.Bool => "bool",
        PropertyValueType.Int32 => "int",
        PropertyValueType.Int64 => "long",
        PropertyValueType.Double => "double",
        PropertyValueType.String => "string",
        PropertyValueType.Bytes => "bytes",
        PropertyValueType.FloatArray => "float[]",
        _ => "unknown",
    };

    private sealed class VertexSchemaEntry
    {
        public Dictionary<string, string> Properties { get; } = new();
        public Dictionary<string, List<string>> Indexes { get; } = new();
    }
}

// ── スキーマ応答の JSON モデル ──────────────────────────────

internal sealed class SchemaResponse
{
    [JsonPropertyName("vertices")]
    public List<VertexSchema> Vertices { get; set; } = [];

    [JsonPropertyName("edges")]
    public List<EdgeSchema> Edges { get; set; } = [];

    [JsonPropertyName("vectorIndexes")]
    public List<VectorIndexSchema> VectorIndexes { get; set; } = [];
}

internal sealed class VertexSchema
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("properties")]
    public Dictionary<string, string> Properties { get; set; } = new();

    [JsonPropertyName("indexes")]
    public Dictionary<string, List<string>> Indexes { get; set; } = new();
}

internal sealed class EdgeSchema
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

internal sealed class VectorIndexSchema
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("dimensions")]
    public int Dimensions { get; set; }

    [JsonPropertyName("metric")]
    public string Metric { get; set; } = "";
}

[JsonSerializable(typeof(SchemaResponse))]
internal partial class SchemaJsonContext : JsonSerializerContext;
