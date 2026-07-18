// Quiver.Samples.Signal — ApplyDyadic による float[] の演算処理。
//
// デモの流れ: テンプレート波形 → トラバーサルによるフィルタ → CosineSimilarityOp によるランキング
// → メタデータ取得. 演算範囲指定およびサブトラバーサル引数のサンプルも記載する。
//
// 実行: dotnet run --project samples/Quiver.Samples.Signal

using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

string dir = Path.Combine(Path.GetTempPath(), "quiver_signal_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    using var db = QuiverDatabase.Open(Path.Combine(dir, "graph.quiver"));

    // ── インデックス定義 (カスタムスコアリングでは HNSW が不要なため FlatOnly) ──
    const string indexName = "Waveform";
    const int dim = 8;
    PropertyKeyId waveformKey;
    using (var schemaTx = db.BeginWriteTransaction())
    {
        waveformKey = schemaTx.EditSchema.GetOrCreatePropertyKey("Waveform");
        schemaTx.Commit();
    }

    db.Vectors.CreateVectorIndex(new VectorIndexSpec(
        Name: indexName,
        EntityKind: EntityKind.Vertex,
        SourcePropertyKeyId: waveformKey,
        Dimensions: dim,
        Metric: DistanceMetric.Cosine,
        ProviderId: "sample-static",
        IndexKind: VectorIndexKind.FlatOnly));

    // ── 合成波形を持つセンサーを投入 ──
    using (var tx = db.BeginWriteTransaction())
    {
        var sensors = new (string Site, string Id, float[] Wave)[]
        {
            ("Tokyo",  "S-001", [0.9f, 0.8f, 0.1f, 0.0f, 0.0f, 0.1f, 0.8f, 0.9f]),
            ("Tokyo",  "S-002", [0.1f, 0.2f, 0.9f, 1.0f, 1.0f, 0.9f, 0.2f, 0.1f]),
            ("Tokyo",  "S-003", [0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f]),
            ("Osaka",  "S-004", [1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f]),
            ("Osaka",  "S-005", [0.0f, 0.0f, 0.0f, 1.0f, 1.0f, 0.0f, 0.0f, 0.0f]),
        };

        foreach (var (site, id, wave) in sensors)
        {
            var nid = tx.CreateVertex("Sensor");
            tx.SetProperty(nid, "Site", PropertyValue.FromString(site));
            tx.SetProperty(nid, "SensorId", PropertyValue.FromString(id));
            db.Vectors.SetVector(EntityKind.Vertex, nid.Value, indexName, wave);
        }

        // 基準パターンとなるテンプレートを float[] プロパティとして保存する。
        var tmpl = tx.CreateVertex("Template");
        tx.SetProperty(tmpl, "Name", PropertyValue.FromString("bell-curve"));
        tx.SetProperty(tmpl, "Pattern",
            PropertyValue.FromFloatArray([0.8f, 0.7f, 0.2f, 0.0f, 0.0f, 0.2f, 0.7f, 0.8f]));

        tx.Commit();
    }

    // ── 1. 基本の ApplyDyadic: 東京のセンサーをコサイン類似度で順位付け ──
    Console.WriteLine("── 1. ApplyDyadic (static b, Tokyo sensors only) ──");
    float[] query = [0.9f, 0.8f, 0.1f, 0.0f, 0.0f, 0.1f, 0.8f, 0.9f];

    using (var tx = db.BeginReadTransaction())
    {
        var g = tx.Query;
        var hits = g.Vertices<SensorVertex>()
            .Has(s => s.Site, "Tokyo")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, query, k: 3)
            .ToList();

        foreach (var sensor in hits)
            Console.WriteLine($"  {sensor.SensorId}  site={sensor.Site}");
    }

    // ── 2. 範囲を限定したスコアリング (先頭 4 次元のみ) ──
    Console.WriteLine();
    Console.WriteLine("── 2. Region-restricted scoring (dims 0..4) ──");
    using (var tx = db.BeginReadTransaction())
    {
        var g = tx.Query;
        var hits = g.Vertices<SensorVertex>()
            .Has(s => s.Site, "Tokyo")
            .ApplyDyadic<CosineSimilarityOp>(
                s => s.Waveform, query,
                regions: [0..4],
                k: 2)
            .ToList();

        foreach (var sensor in hits)
            Console.WriteLine($"  {sensor.SensorId}  site={sensor.Site}");
    }

    // ── 3. トラバーサルで b を指定 (グラフ内の参照ベクトル) ──
    Console.WriteLine();
    Console.WriteLine("── 3. Traversal b (Template 'bell-curve' as reference) ──");
    using (var tx = db.BeginReadTransaction())
    {
        var g = tx.Query;

        var templateB = g.Vertices<TemplateVertex>()
            .Has(t => t.Name, "bell-curve")
            .Values(t => t.Pattern);

        var hits = g.Vertices<SensorVertex>()
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, templateB, k: 3)
            .ToList();

        foreach (var sensor in hits)
            Console.WriteLine($"  {sensor.SensorId}  site={sensor.Site}");
    }

    // ── 4. 全センサーに対する内積 ──
    Console.WriteLine();
    Console.WriteLine("── 4. DotProductOp (all sensors) ──");
    using (var tx = db.BeginReadTransaction())
    {
        var g = tx.Query;
        var hits = g.Vertices<SensorVertex>()
            .ApplyDyadic<DotProductOp>(s => s.Waveform, query, k: 5)
            .ToList();

        foreach (var sensor in hits)
            Console.WriteLine($"  {sensor.SensorId}  site={sensor.Site}");
    }

    Console.WriteLine();
    Console.WriteLine("Done.");
}
finally
{
    if (Directory.Exists(dir))
        Directory.Delete(dir, recursive: true);
}

// ── 手書きの IGraphVertex 型 (サンプルでは Source Generator を使わない) ──

sealed class SensorVertex : IGraphVertex<SensorVertex>
{
    public string Site { get; set; } = "";
    public string SensorId { get; set; } = "";
    public float[] Waveform { get; set; } = [];

    public static string GraphLabel => "Sensor";

    public static VertexId Insert(IWriteTransaction tx, SensorVertex entity)
    {
        var id = tx.CreateVertex(GraphLabel);
        tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));
        tx.SetProperty(id, "SensorId", PropertyValue.FromString(entity.SensorId));
        return id;
    }

    public static VertexId InsertIndexed(IWriteTransaction tx, SensorVertex entity) => Insert(tx, entity);

    public static SensorVertex Load(IReadTransaction tx, VertexId id)
    {
        var site = tx.GetProperty(id, "Site");
        var sid = tx.GetProperty(id, "SensorId");
        return new()
        {
            Site = System.Text.Encoding.UTF8.GetString(site.Utf8StringValue),
            SensorId = System.Text.Encoding.UTF8.GetString(sid.Utf8StringValue),
        };
    }

    public static void Update(IWriteTransaction tx, VertexId id, SensorVertex entity)
    {
        tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));
        tx.SetProperty(id, "SensorId", PropertyValue.FromString(entity.SensorId));
    }

    public static void Delete(IWriteTransaction tx, VertexId id) => tx.DeleteVertex(id);
}

sealed class TemplateVertex : IGraphVertex<TemplateVertex>
{
    public string Name { get; set; } = "";
    public float[] Pattern { get; set; } = [];

    public static string GraphLabel => "Template";

    public static VertexId Insert(IWriteTransaction tx, TemplateVertex entity)
    {
        var id = tx.CreateVertex(GraphLabel);
        tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
        tx.SetProperty(id, "Pattern", PropertyValue.FromFloatArray(entity.Pattern));
        return id;
    }

    public static VertexId InsertIndexed(IWriteTransaction tx, TemplateVertex entity) => Insert(tx, entity);

    public static TemplateVertex Load(IReadTransaction tx, VertexId id)
    {
        var nameVal = tx.GetProperty(id, "Name");
        var patternVal = tx.GetProperty(id, "Pattern");
        return new()
        {
            Name = System.Text.Encoding.UTF8.GetString(nameVal.Utf8StringValue),
            Pattern = patternVal.Type == PropertyValueType.FloatArray
                ? patternVal.FloatArrayValue.ToArray()
                : [],
        };
    }

    public static void Update(IWriteTransaction tx, VertexId id, TemplateVertex entity)
    {
        tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
        tx.SetProperty(id, "Pattern", PropertyValue.FromFloatArray(entity.Pattern));
    }

    public static void Delete(IWriteTransaction tx, VertexId id) => tx.DeleteVertex(id);
}
