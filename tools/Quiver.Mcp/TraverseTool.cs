// traverse ツール — 1 回の MCP 呼び出しでグラフ検索 + 最大 2-hop 走査を実行する。
//
// 設計上の重要な判断:
//
// ■ パラメータの flat 化
//   MCP ツールの引数を個別の string/int パラメータにしている。
//   本来は { start: {...}, hops: [...] } のような構造化 JSON が自然だが、
//   ローカル LLM (LM Studio 等) は JSON ダブルエンコード (文字列中の JSON) を
//   誤りやすい。flat な引数ならスキーマだけで正しく組み立てられる。
//
// ■ PropertyValue の ref struct 制約
//   Quiver の PropertyValue は ref struct であり、ラムダキャプチャや
//   コレクション格納ができない。そのため ReadVertex / PassesFilter では
//   値を即座にコピーして PathVertex (sealed record) に詰め替えている。
//   文字列は Utf8StringValue (ReadOnlySpan<byte>) なので都度 UTF-8 デコードが必要。
//
// ■ パス展開戦略
//   各 hop でパス (List<PathVertex>) を分岐コピーする BFS 方式。
//   最大 2-hop かつ MCP の result limit があるため、メモリ爆発リスクは許容範囲。

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

namespace Quiver.Mcp;

[McpServerToolType]
internal static class TraverseTool
{
    [McpServerTool(Name = "traverse"), Description(
        "Search and traverse the graph. " +
        "Start from vertices found by fulltext/vector/label/id, then optionally follow edges (up to 2 hops).")]
    public static async Task<string> Execute(
        McpContext ctx,
        [Description("How to find start vertices: fulltext, vector, label, or id")]
        string startType,
        [Description("Search text (required for fulltext and vector)")]
        string? query = null,
        [Description("Filter by vertex label")]
        string? label = null,
        [Description("Comma-separated vertex IDs (for id lookup)")]
        string? ids = null,
        [Description("Max start vertices to retrieve (default 10)")]
        int? startLimit = null,
        [Description("Edge type for first hop")]
        string? hop1Edge = null,
        [Description("Direction for first hop: out, in, or both (default both)")]
        string? hop1Direction = null,
        [Description("Property filter for first hop as JSON, e.g. {\"year\":{\"gte\":2020}}")]
        string? hop1Filter = null,
        [Description("Edge type for second hop")]
        string? hop2Edge = null,
        [Description("Direction for second hop: out, in, or both (default both)")]
        string? hop2Direction = null,
        [Description("Property filter for second hop as JSON")]
        string? hop2Filter = null,
        [Description("Return mode: terminal (default) or path")]
        string? returnMode = null,
        [Description("Comma-separated property names to include in results (default: all)")]
        string? returnProperties = null,
        [Description("Max results to return (default 100)")]
        int? returnLimit = null)
    {
        // flat パラメータを内部構造体に組み立てる
        var start = new StartSpec
        {
            Type = startType,
            Query = query,
            Label = label,
            Ids = ids?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Limit = startLimit,
        };

        // hop パラメータが 1 つでも指定されていれば hop を構築する
        var hops = new List<HopSpec>();
        if (hop1Edge is not null || hop1Direction is not null)
        {
            hops.Add(new HopSpec
            {
                Edge = hop1Edge,
                Direction = hop1Direction,
                Filter = ParseFilterJson(hop1Filter),
            });
        }
        if (hop2Edge is not null || hop2Direction is not null)
        {
            hops.Add(new HopSpec
            {
                Edge = hop2Edge,
                Direction = hop2Direction,
                Filter = ParseFilterJson(hop2Filter),
            });
        }

        var wantedPropsList = returnProperties?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var db = ctx.Db;
        var allPropKeys = db.Schema.ListPropertyKeys();

        // 起点Vertexを解決 (vector の場合は embedding API 呼び出しがあるため async)
        var startVertices = await ResolveStartVertices(start, db, ctx.Embedder);

        using var tx = db.BeginReadOnlyTransaction();
        var wantedProps = wantedPropsList ?? allPropKeys.ToList();

        // 起点Vertexそれぞれを「長さ 1 のパス」として初期化
        var paths = startVertices
            .Select(n => new List<PathVertex> { ReadVertex(n, tx, db.Schema, wantedProps) })
            .ToList();

        // 各 hop でパスを伸長する (BFS: 全既存パスの末端から隣接Vertexへ分岐)
        foreach (var hop in hops)
            paths = ExecuteHop(paths, hop, tx, db.Schema, wantedProps);

        int limit = returnLimit ?? 100;

        // path モード: 経由Vertex含む完全パスを返す
        if (returnMode == "path")
        {
            var result = paths
                .Take(limit)
                .Select(p => new JsonObject
                {
                    ["path"] = new JsonArray(p.Select(VertexToJson).ToArray())
                })
                .ToList();
            return JsonSerializer.Serialize(result, TraverseJsonContext.Default.ListJsonObject);
        }
        // terminal モード (既定): 末端Vertexのみ重複排除して返す
        else
        {
            var seen = new HashSet<long>();
            var result = new List<JsonObject>();
            foreach (var path in paths)
            {
                if (path.Count == 0) continue;
                var terminal = path[^1];
                if (seen.Add(terminal.Id))
                {
                    result.Add(VertexToJson(terminal));
                    if (result.Count >= limit) break;
                }
            }
            return JsonSerializer.Serialize(result, TraverseJsonContext.Default.ListJsonObject);
        }
    }

    private static Dictionary<string, JsonElement>? ParseFilterJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
    }

    // ── 起点Vertex解決 ──────────────────────────────────────

    // startType に応じて Quiver の検索 API を使い分ける。
    // LLM は type を指定するだけで、内部で機械的に適切な API に変換する。
    private static async Task<List<VertexId>> ResolveStartVertices(
        StartSpec start, QuiverDatabase db, OpenAiEmbedder? embedder)
    {
        var type = start.Type?.ToLowerInvariant() ?? "label";
        int limit = start.Limit ?? 10;

        using var tx = db.BeginReadOnlyTransaction();
        var g = tx.G(db.Schema);

        switch (type)
        {
            case "fulltext":
            {
                if (string.IsNullOrEmpty(start.Query))
                    throw new ArgumentException("'query' is required for fulltext search.");
                var ftIndexName = FindFullTextIndex(db.Schema, start.Label);
                return g.Search(ftIndexName, start.Query, limit).ToList();
            }
            case "vector":
            {
                if (string.IsNullOrEmpty(start.Query))
                    throw new ArgumentException("'query' is required for vector search.");
                if (embedder is null)
                    throw new InvalidOperationException(
                        "Embedding is not configured. Set 'embedding' in quiver-mcp.json.");
                // テキストを embedding API でベクトル化し、KNN 検索にかける
                var vectors = await embedder.EmbedAsync([start.Query]);
                var vecIndexName = FindVectorIndex(db.Vectors, start.Label);
                return g.Knn(vecIndexName, vectors[0], limit).ToList();
            }
            case "label":
            {
                var traversal = g.Vertices();
                if (!string.IsNullOrEmpty(start.Label))
                    traversal = traversal.HasLabel(start.Label);
                traversal = ApplyTraversalFilters(traversal, start.Filter);
                return traversal.Limit(limit).ToList();
            }
            case "id":
            {
                if (start.Ids is null or { Count: 0 })
                    throw new ArgumentException("'ids' is required for id lookup.");
                return start.Ids
                    .Select(id => new VertexId(long.Parse(id)))
                    .Where(tx.VertexExists)
                    .ToList();
            }
            default:
                throw new ArgumentException(
                    $"Unknown start type: '{type}'. Use fulltext, vector, label, or id.");
        }
    }

    // ── Hop 実行 ────────────────────────────────────────────

    // 全パスの末端Vertexからエッジを辿り、フィルタを通過した隣接Vertexで新しいパスを生成する。
    // EdgeReadHandle から Source/Target を取り出し、
    // 現在Vertex側でない方を隣接Vertexとみなす (both 方向対応)。
    private static List<List<PathVertex>> ExecuteHop(
        List<List<PathVertex>> paths, HopSpec hop,
        IGraphTransaction tx, ISchemaApi schema, List<string> wantedProps)
    {
        var direction = ParseDirection(hop.Direction);
        var result = new List<List<PathVertex>>();

        foreach (var path in paths)
        {
            var current = path[^1];
            var vertexId = new VertexId(current.Id);
            var edges = tx.EnumerateEdges(vertexId, direction, hop.Edge);

            while (edges.MoveNext())
            {
                var edge = edges.Current;
                // both 方向の場合、Source/Target のうち自分でない方が隣接Vertex
                var neighbor = edge.Source == vertexId ? edge.Target : edge.Source;

                if (!PassesFilter(neighbor, hop.Filter, tx))
                    continue;

                // パスをコピーして隣接Vertexを追加 (BFS 分岐)
                var newPath = new List<PathVertex>(path)
                {
                    ReadVertex(neighbor, tx, schema, wantedProps)
                };
                result.Add(newPath);
            }
        }

        return result;
    }

    // ── フィルタ評価 ────────────────────────────────────────

    // フィルタは { "propKey": value } (eq 省略形) または { "propKey": { "op": value } } 形式。
    // PropertyValue は ref struct なので、この関数の呼び出しスコープ内で値を消費し切る必要がある。
    private static bool PassesFilter(
        VertexId vertexId, Dictionary<string, JsonElement>? filter, IGraphTransaction tx)
    {
        if (filter is null or { Count: 0 }) return true;

        foreach (var (key, ops) in filter)
        {
            if (!tx.HasProperty(vertexId, key)) return false;
            var value = tx.GetProperty(vertexId, key);

            if (ops.ValueKind == JsonValueKind.Object)
            {
                // { "year": { "gte": 2020 } } — 演算子指定
                foreach (var op in ops.EnumerateObject())
                {
                    if (!EvaluateOp(op.Name, op.Value, value))
                        return false;
                }
            }
            else
            {
                // { "name": "Keanu" } — 暗黙 eq
                if (!EvaluateOp("eq", ops, value))
                    return false;
            }
        }
        return true;
    }

    // 対応演算子: eq, neq, gt, gte, lt, lte, contains, in
    private static bool EvaluateOp(string op, JsonElement expected, PropertyValue actual)
    {
        return op.ToLowerInvariant() switch
        {
            "eq" => EqualsValue(expected, actual),
            "neq" => !EqualsValue(expected, actual),
            "gt" => CompareValue(actual, expected) > 0,
            "gte" => CompareValue(actual, expected) >= 0,
            "lt" => CompareValue(actual, expected) < 0,
            "lte" => CompareValue(actual, expected) <= 0,
            "contains" => actual.Type == PropertyValueType.String &&
                          Encoding.UTF8.GetString(actual.Utf8StringValue)
                              .Contains(expected.GetString() ?? "", StringComparison.OrdinalIgnoreCase),
            "in" => expected.ValueKind == JsonValueKind.Array &&
                    MatchesAny(expected, actual),
            _ => true,
        };
    }

    private static bool MatchesAny(JsonElement array, PropertyValue actual)
    {
        foreach (var e in array.EnumerateArray())
        {
            if (EqualsValue(e, actual)) return true;
        }
        return false;
    }

    // PropertyValue の型に応じて JsonElement と比較する。
    // 文字列は Utf8StringValue (ReadOnlySpan<byte>) なので UTF-8 デコードが必要。
    private static bool EqualsValue(JsonElement expected, PropertyValue actual) => actual.Type switch
    {
        PropertyValueType.String => expected.ValueKind == JsonValueKind.String &&
                                    Encoding.UTF8.GetString(actual.Utf8StringValue) == expected.GetString(),
        PropertyValueType.Int64 => expected.TryGetInt64(out var l) && actual.Int64Value == l,
        PropertyValueType.Int32 => expected.TryGetInt32(out var i) && actual.Int32Value == i,
        PropertyValueType.Double => expected.TryGetDouble(out var d) &&
                                    Math.Abs(actual.DoubleValue - d) < 1e-9,
        PropertyValueType.Bool => expected.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                  actual.BoolValue == expected.GetBoolean(),
        _ => false,
    };

    private static int CompareValue(PropertyValue actual, JsonElement expected) => actual.Type switch
    {
        PropertyValueType.Int64 when expected.TryGetInt64(out var l) => actual.Int64Value.CompareTo(l),
        PropertyValueType.Int32 when expected.TryGetInt32(out var i) => actual.Int32Value.CompareTo(i),
        PropertyValueType.Double when expected.TryGetDouble(out var d) => actual.DoubleValue.CompareTo(d),
        PropertyValueType.String when expected.ValueKind == JsonValueKind.String =>
            string.Compare(Encoding.UTF8.GetString(actual.Utf8StringValue),
                expected.GetString(), StringComparison.Ordinal),
        _ => 0,
    };

    // ── Gremlin トラバーサルへのフィルタ適用 ────────────────

    // label 検索の場合のみ、Quiver の Gremlin 風 API (Has + P 述語) を直接使う。
    // hop のフィルタは PassesFilter でVertex単位に評価する (Gremlin API は使わない)。
    private static GraphTraversal<VertexId> ApplyTraversalFilters(
        GraphTraversal<VertexId> traversal, Dictionary<string, JsonElement>? filter)
    {
        if (filter is null) return traversal;

        foreach (var (key, ops) in filter)
        {
            if (ops.ValueKind == JsonValueKind.Object)
            {
                foreach (var op in ops.EnumerateObject())
                    traversal = ApplySingleFilter(traversal, key, op.Name, op.Value);
            }
            else
            {
                traversal = ApplySingleFilter(traversal, key, "eq", ops);
            }
        }
        return traversal;
    }

    private static GraphTraversal<VertexId> ApplySingleFilter(
        GraphTraversal<VertexId> traversal, string key, string op, JsonElement value)
    {
        // JsonElement の型を見て適切な Has オーバーロードに振り分ける。
        // int64 → double のフォールバックにより、数値は整数優先で解釈される。
        return op.ToLowerInvariant() switch
        {
            "eq" when value.ValueKind == JsonValueKind.String =>
                traversal.Has(key, value.GetString()!),
            "eq" when value.TryGetInt64(out var l) =>
                traversal.Has(key, l),
            "eq" when value.TryGetDouble(out var d) =>
                traversal.Has(key, d),
            "eq" when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                traversal.Has(key, value.GetBoolean()),
            "gt" when value.TryGetInt64(out var l) => traversal.Has(key, P.Gt(l)),
            "gte" when value.TryGetInt64(out var l) => traversal.Has(key, P.Gte(l)),
            "lt" when value.TryGetInt64(out var l) => traversal.Has(key, P.Lt(l)),
            "lte" when value.TryGetInt64(out var l) => traversal.Has(key, P.Lte(l)),
            "gt" when value.TryGetDouble(out var d) => traversal.Has(key, P.Gt(d)),
            "gte" when value.TryGetDouble(out var d) => traversal.Has(key, P.Gte(d)),
            "lt" when value.TryGetDouble(out var d) => traversal.Has(key, P.Lt(d)),
            "lte" when value.TryGetDouble(out var d) => traversal.Has(key, P.Lte(d)),
            "contains" when value.ValueKind == JsonValueKind.String =>
                traversal.Has(key, P.Contains(value.GetString()!)),
            "in" when value.ValueKind == JsonValueKind.Array =>
                traversal.Has(key, P.Within(value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray())),
            _ => traversal,
        };
    }

    // ── Vertex読み取り・JSON 変換 ───────────────────────────

    // PropertyValue は ref struct のため、値を即座に object にコピーする。
    // EnumerateProperties (PropertyKeyId 逆引き不要) ではなく、
    // キー名指定の HasProperty/GetProperty を使うことで、欲しいプロパティだけを取得する。
    private static PathVertex ReadVertex(
        VertexId vertexId, IGraphTransaction tx, ISchemaApi schema, List<string> wantedProps)
    {
        var label = tx.GetVertexLabel(vertexId) ?? "";
        var props = new Dictionary<string, object?>();

        foreach (var key in wantedProps)
        {
            if (!tx.HasProperty(vertexId, key)) continue;
            var pv = tx.GetProperty(vertexId, key);
            props[key] = ExtractValue(pv);
        }

        return new PathVertex(vertexId.Value, label, props);
    }

    // ref struct → boxed object への変換。文字列は UTF-8 バイト列からデコードする。
    private static object? ExtractValue(PropertyValue pv) => pv.Type switch
    {
        PropertyValueType.String => Encoding.UTF8.GetString(pv.Utf8StringValue),
        PropertyValueType.Int32 => pv.Int32Value,
        PropertyValueType.Int64 => pv.Int64Value,
        PropertyValueType.Double => pv.DoubleValue,
        PropertyValueType.Bool => pv.BoolValue,
        _ => null,
    };

    private static JsonObject VertexToJson(PathVertex vertex)
    {
        var obj = new JsonObject
        {
            ["id"] = vertex.Id.ToString(),
            ["label"] = vertex.Label,
        };
        foreach (var (key, val) in vertex.Properties)
        {
            obj[key] = val switch
            {
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                bool b => JsonValue.Create(b),
                _ => val?.ToString() is { } ts ? JsonValue.Create(ts) : null,
            };
        }
        return obj;
    }

    // ── ヘルパー ────────────────────────────────────────────

    private static Direction ParseDirection(string? dir) => dir?.ToLowerInvariant() switch
    {
        "out" => Direction.Outgoing,
        "in" => Direction.Incoming,
        "both" => Direction.Both,
        _ => Direction.Both,
    };

    // label が指定されていれば該当ラベルの FTS インデックスを探し、
    // 無ければ DB 内の最初の FTS インデックスにフォールバックする
    private static string FindFullTextIndex(ISchemaApi schema, string? label)
    {
        var ftIndexes = schema.ListFullTextIndexes();
        if (!string.IsNullOrEmpty(label))
        {
            var match = ftIndexes.FirstOrDefault(i => i.Label == label);
            if (match is not null) return match.Name;
        }
        return ftIndexes.Count > 0
            ? ftIndexes[0].Name
            : throw new InvalidOperationException("No full-text index found.");
    }

    private static string FindVectorIndex(IVectorStore vectors, string? label)
    {
        var vecIndexes = vectors.ListVectorIndexes();
        return vecIndexes.Count > 0
            ? vecIndexes[0].Name
            : throw new InvalidOperationException("No vector index found.");
    }

    // hop 間で受け渡すVertex情報。ref struct の PropertyValue を保持できないため、
    // 値は object にコピー済み。
    private sealed record PathVertex(long Id, string Label, Dictionary<string, object?> Properties);
}

// ── 内部モデル ──────────────────────────────────────────────

internal sealed class StartSpec
{
    public string? Type { get; set; }
    public string? Query { get; set; }
    public string? Label { get; set; }
    public List<string>? Ids { get; set; }
    public Dictionary<string, JsonElement>? Filter { get; set; }
    public int? Limit { get; set; }
}

internal sealed class HopSpec
{
    public string? Edge { get; set; }
    public string? Direction { get; set; }
    public Dictionary<string, JsonElement>? Filter { get; set; }
}

[JsonSerializable(typeof(List<JsonObject>))]
internal partial class TraverseJsonContext : JsonSerializerContext;
