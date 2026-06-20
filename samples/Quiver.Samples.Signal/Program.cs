// Quiver.Samples.Signal — ApplyDyadic (custom dyadic scoring on stored waveforms).
//
// Demonstrates: template waveform → graph filter → CosineSimilarityOp ranking
// → metadata retrieval. Also shows region-restricted scoring and traversal-based b.
//
// Run: dotnet run --project samples/Quiver.Samples.Signal

using Quiver;
using Quiver.Api;
using Quiver.Core;
using Quiver.Storage.Records;

string dir = Path.Combine(Path.GetTempPath(), "quiver_signal_" + Guid.NewGuid().ToString("N")[..8]);
try
{
    using var db = GraphDatabase.Open(Path.Combine(dir, "graph.quiver"));

    // ── Index definition (FlatOnly — HNSW not needed for custom scoring) ──
    const string indexName = "Waveform";
    const int dim = 8;
    var waveformKey = db.Schema.GetOrCreatePropertyKey("Waveform");
    db.Vectors.CreateVectorIndex(new VectorIndexSpec(
        Name: indexName,
        EntityKind: EntityKind.Node,
        SourcePropertyKeyId: waveformKey,
        Dimensions: dim,
        Metric: DistanceMetric.Cosine,
        ProviderId: "sample-static",
        IndexKind: VectorIndexKind.FlatOnly));

    // ── Populate sensors with synthetic waveforms ──
    using (var tx = db.BeginTransaction())
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
            var nid = tx.CreateNode("Sensor");
            tx.SetProperty(nid, "Site", PropertyValue.FromString(site));
            tx.SetProperty(nid, "SensorId", PropertyValue.FromString(id));
            db.Vectors.SetVector(EntityKind.Node, nid.Value, indexName, wave);
        }

        // Template (reference pattern) stored as a float[] property
        var tmpl = tx.CreateNode("Template");
        tx.SetProperty(tmpl, "Name", PropertyValue.FromString("bell-curve"));
        tx.SetProperty(tmpl, "Pattern",
            PropertyValue.FromFloatArray([0.8f, 0.7f, 0.2f, 0.0f, 0.0f, 0.2f, 0.7f, 0.8f]));

        tx.Commit();
    }

    // ── 1. Basic ApplyDyadic: rank Tokyo sensors by cosine similarity ──
    Console.WriteLine("── 1. ApplyDyadic (static b, Tokyo sensors only) ──");
    float[] query = [0.9f, 0.8f, 0.1f, 0.0f, 0.0f, 0.1f, 0.8f, 0.9f];

    using (var tx = db.BeginReadOnlyTransaction())
    {
        var g = tx.G(db.Schema);
        var hits = g.Nodes<SensorNode>()
            .Has(s => s.Site, "Tokyo")
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, query, k: 3)
            .ToList();

        foreach (var sensor in hits)
            Console.WriteLine($"  {sensor.SensorId}  site={sensor.Site}");
    }

    // ── 2. Region-restricted scoring (first 4 dimensions only) ──
    Console.WriteLine();
    Console.WriteLine("── 2. Region-restricted scoring (dims 0..4) ──");
    using (var tx = db.BeginReadOnlyTransaction())
    {
        var g = tx.G(db.Schema);
        var hits = g.Nodes<SensorNode>()
            .Has(s => s.Site, "Tokyo")
            .ApplyDyadic<CosineSimilarityOp>(
                s => s.Waveform, query,
                regions: [0..4],
                k: 2)
            .ToList();

        foreach (var sensor in hits)
            Console.WriteLine($"  {sensor.SensorId}  site={sensor.Site}");
    }

    // ── 3. Traversal-based b (reference vector from graph) ──
    Console.WriteLine();
    Console.WriteLine("── 3. Traversal b (Template 'bell-curve' as reference) ──");
    using (var tx = db.BeginReadOnlyTransaction())
    {
        var g = tx.G(db.Schema);

        var templateB = g.Nodes<TemplateNode>()
            .Has(t => t.Name, "bell-curve")
            .Values(t => t.Pattern);

        var hits = g.Nodes<SensorNode>()
            .ApplyDyadic<CosineSimilarityOp>(s => s.Waveform, templateB, k: 3)
            .ToList();

        foreach (var sensor in hits)
            Console.WriteLine($"  {sensor.SensorId}  site={sensor.Site}");
    }

    // ── 4. Dot product on all sensors ──
    Console.WriteLine();
    Console.WriteLine("── 4. DotProductOp (all sensors) ──");
    using (var tx = db.BeginReadOnlyTransaction())
    {
        var g = tx.G(db.Schema);
        var hits = g.Nodes<SensorNode>()
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

// ── Hand-coded IGraphNode types (no SourceGenerator needed for the sample) ──

sealed class SensorNode : IGraphNode<SensorNode>
{
    public string Site { get; set; } = "";
    public string SensorId { get; set; } = "";
    public float[] Waveform { get; set; } = [];

    public static string GraphLabel => "Sensor";

    public static NodeId Insert(IGraphTransaction tx, SensorNode entity)
    {
        var id = tx.CreateNode(GraphLabel);
        tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));
        tx.SetProperty(id, "SensorId", PropertyValue.FromString(entity.SensorId));
        return id;
    }

    public static NodeId InsertIndexed(IGraphTransaction tx, SensorNode entity) => Insert(tx, entity);

    public static SensorNode Load(IGraphTransaction tx, NodeId id)
    {
        var site = tx.GetProperty(id, "Site");
        var sid = tx.GetProperty(id, "SensorId");
        return new()
        {
            Site = System.Text.Encoding.UTF8.GetString(site.Utf8StringValue),
            SensorId = System.Text.Encoding.UTF8.GetString(sid.Utf8StringValue),
        };
    }

    public static void Update(IGraphTransaction tx, NodeId id, SensorNode entity)
    {
        tx.SetProperty(id, "Site", PropertyValue.FromString(entity.Site));
        tx.SetProperty(id, "SensorId", PropertyValue.FromString(entity.SensorId));
    }

    public static void Delete(IGraphTransaction tx, NodeId id) => tx.DeleteNode(id);
}

sealed class TemplateNode : IGraphNode<TemplateNode>
{
    public string Name { get; set; } = "";
    public float[] Pattern { get; set; } = [];

    public static string GraphLabel => "Template";

    public static NodeId Insert(IGraphTransaction tx, TemplateNode entity)
    {
        var id = tx.CreateNode(GraphLabel);
        tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
        tx.SetProperty(id, "Pattern", PropertyValue.FromFloatArray(entity.Pattern));
        return id;
    }

    public static NodeId InsertIndexed(IGraphTransaction tx, TemplateNode entity) => Insert(tx, entity);

    public static TemplateNode Load(IGraphTransaction tx, NodeId id)
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

    public static void Update(IGraphTransaction tx, NodeId id, TemplateNode entity)
    {
        tx.SetProperty(id, "Name", PropertyValue.FromString(entity.Name));
        tx.SetProperty(id, "Pattern", PropertyValue.FromFloatArray(entity.Pattern));
    }

    public static void Delete(IGraphTransaction tx, NodeId id) => tx.DeleteNode(id);
}
