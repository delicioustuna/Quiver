// schema ツール — DB のグラフスキーマ (ラベル・プロパティ・インデックス) を返す。
// LLM は最初にこのツールを呼び、返却されたスキーマを見て traverse の引数を組み立てる。

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Quiver.Api;
using Quiver.Storage.Records;

namespace Quiver.Mcp;

[McpServerToolType]
internal static class SchemaTool
{
    [McpServerTool(Name = "schema"), Description("Returns the graph schema: node labels with properties and indexes, edge types, and vector indexes.")]
    public static string Execute(McpContext ctx)
    {
        var db = ctx.Db;
        var schema = db.Schema;
        var vectors = db.Vectors;

        var labels = schema.ListLabels();
        var relTypes = schema.ListRelationshipTypes();
        var allPropKeys = schema.ListPropertyKeys();
        var indexes = schema.ListIndexes();
        var ftIndexes = schema.ListFullTextIndexes();
        var vecIndexes = vectors.ListVectorIndexes();

        // ラベルごとにプロパティとインデックスを集約する
        var nodeMap = new Dictionary<string, NodeSchemaEntry>();
        foreach (var label in labels)
            nodeMap[label] = new NodeSchemaEntry();

        foreach (var idx in indexes)
        {
            if (nodeMap.TryGetValue(idx.Label, out var entry))
                entry.Indexes["btree"] = entry.Indexes.GetValueOrDefault("btree", []).Append(idx.PropertyKey).ToList();
        }

        foreach (var ftIdx in ftIndexes)
        {
            if (nodeMap.TryGetValue(ftIdx.Label, out var entry))
                entry.Indexes["fulltext"] = entry.Indexes.GetValueOrDefault("fulltext", []).Append(ftIdx.PropertyKey).ToList();
        }

        // プロパティの名前と型を実データから推定する (下記メソッド参照)
        DiscoverProperties(db, nodeMap, allPropKeys);

        var result = new SchemaResponse
        {
            Nodes = nodeMap.Select(kv => new NodeSchema
            {
                Label = kv.Key,
                Properties = kv.Value.Properties,
                Indexes = kv.Value.Indexes,
            }).ToList(),
            Edges = relTypes.Select(rt => new EdgeSchema { Label = rt }).ToList(),
            VectorIndexes = vecIndexes.Select(v => new VectorIndexSchema
            {
                Name = v.Name,
                Dimensions = v.Dimensions,
                Metric = v.Metric.ToString().ToLowerInvariant(),
            }).ToList(),
        };

        return JsonSerializer.Serialize(result, SchemaJsonContext.Default.SchemaResponse);
    }

    // Quiver はスキーマレス設計のため、プロパティの型情報をカタログに持たない。
    // PropertyKeyId → キー名の逆引き API も無いので、
    // ラベルごとに実ノードをサンプリングし、全プロパティキーに対して HasProperty/GetProperty で
    // 存在するプロパティ名とその型を発見する。
    private static void DiscoverProperties(
        GraphDatabase db, Dictionary<string, NodeSchemaEntry> nodeMap,
        IReadOnlyList<string> allPropKeys)
    {
        const int sampleSize = 50;
        using var tx = db.BeginReadOnlyTransaction();
        var g = tx.G(db.Schema);

        foreach (var (label, entry) in nodeMap)
        {
            var nodes = g.Nodes().HasLabel(label).Limit(sampleSize).ToList();
            foreach (var nodeId in nodes)
            {
                foreach (var key in allPropKeys)
                {
                    if (entry.Properties.ContainsKey(key)) continue;
                    if (!tx.HasProperty(nodeId, key)) continue;
                    var value = tx.GetProperty(nodeId, key);
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

    private sealed class NodeSchemaEntry
    {
        public Dictionary<string, string> Properties { get; } = new();
        public Dictionary<string, List<string>> Indexes { get; } = new();
    }
}

// ── スキーマ応答の JSON モデル ──────────────────────────────

internal sealed class SchemaResponse
{
    [JsonPropertyName("nodes")]
    public List<NodeSchema> Nodes { get; set; } = [];

    [JsonPropertyName("edges")]
    public List<EdgeSchema> Edges { get; set; } = [];

    [JsonPropertyName("vectorIndexes")]
    public List<VectorIndexSchema> VectorIndexes { get; set; } = [];
}

internal sealed class NodeSchema
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
