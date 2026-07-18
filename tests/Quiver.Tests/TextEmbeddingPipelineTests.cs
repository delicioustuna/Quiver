using FluentAssertions;
using Quiver.Core;
using Quiver.Embedding;
using Quiver.Embedding.Providers;
using Quiver.Embedding.Text;
using Quiver.Text;
using Xunit;

namespace Quiver.Tests;

/// <summary>
/// <see cref="TextEmbeddingPipeline"/> のテキスト入力、コミット後フック、
/// プロバイダー、ベクトルストアまでをエンドツーエンドに検証する。
/// <c>ScanAndEnqueueAsync</c> による取りこぼし回収も対象とする。
/// テストを外部環境から隔離するため、決定的なモックプロバイダーを使う。
/// </summary>
public sealed class TextEmbeddingPipelineTests : IDisposable
{
    private const string IndexName = "title-embed";
    private const string SourceProp = "title";
    private const int Dim = 4;

    private readonly string _dir;
    private readonly QuiverDatabase _db;
    private readonly InMemoryVectorStore _vectors = new();
    private readonly JsonFileVectorCatalog _catalog;
    private readonly GraphEngineAdapter _engine;

    public TextEmbeddingPipelineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quiver_vec4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = QuiverDatabase.Open(System.IO.Path.Combine(_dir, "graph.quiver"));
        _catalog = new JsonFileVectorCatalog(Path.Combine(_dir, "vector_catalog.json"));
        _engine = new GraphEngineAdapter(_db, _vectors, _catalog);

        var keyId = _db.EditSchema(schema => schema.GetOrCreatePropertyKey(SourceProp));
        _vectors.CreateVectorIndex(new VectorIndexSpec(
            IndexName, EntityKind.Vertex, keyId, Dim,
            DistanceMetric.Cosine, "mock", null));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private TextEmbeddingPipeline MakePipeline(MockEmbeddingProvider provider)
        => new(
            _engine,
            provider,
            new MinimalNormalizer(),
            new GraphemeTextTruncator(),
            new VectorCatalogEmbeddingTaskLog(_catalog),
            new NoRetryPolicy());

    [Fact]
    public async Task EnqueueOnCommit_fires_after_commit_and_writes_vector()
    {
        var provider = new MockEmbeddingProvider(Dim);
        await using var pipeline = MakePipeline(provider);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var worker = pipeline.RunAsync(cts.Token);

        VertexId vertex;
        using (var tx = _db.BeginWriteTransaction())
        {
            vertex = tx.CreateVertex("Page");
            tx.SetProperty(vertex, SourceProp, Storage.Records.PropertyValue.FromString("hello world"));
            pipeline.EnqueueOnCommit(tx,
                EntityRef.From(vertex),
                IndexName,
                "hello world");
            tx.Commit();
        }

        await pipeline.WhenDrainedAsync(cts.Token);

        var query = provider.Vectorize("hello world");
        using var cursor = _vectors.KnnSearch(IndexName, query, k: 1);
        cursor.MoveNext().Should().BeTrue();
        cursor.Current.EntityId.Should().Be(vertex.Sequence); // vector binding キーは slot Sequence
        provider.CallCount.Should().Be(1);

        cts.Cancel();
        await worker;
    }

    [Fact]
    public async Task EnqueueOnCommit_does_not_fire_on_rollback()
    {
        var provider = new MockEmbeddingProvider(Dim);
        await using var pipeline = MakePipeline(provider);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var worker = pipeline.RunAsync(cts.Token);

        using (var tx = _db.BeginWriteTransaction())
        {
            var vertex = tx.CreateVertex("Page");
            tx.SetProperty(vertex, SourceProp, Storage.Records.PropertyValue.FromString("dropped"));
            pipeline.EnqueueOnCommit(tx,
                EntityRef.From(vertex),
                IndexName,
                "dropped");
            tx.Rollback();
        }

        // Give the worker a moment; nothing should hit it.
        await Task.Delay(50);
        provider.CallCount.Should().Be(0);
        pipeline.PendingTasks.Should().Be(0);

        cts.Cancel();
        await worker;
    }

    [Fact]
    public async Task ScanAndEnqueueAsync_picks_up_pre_existing_vertices()
    {
        // Pretend a prior process committed vertices but never enqueued embeddings
        // (Z' scenario: commit durable, hook never fired).
        var ids = new List<long>();
        using (var tx = _db.BeginWriteTransaction())
        {
            for (int i = 0; i < 3; i++)
            {
                var nid = tx.CreateVertex("Page");
                tx.SetProperty(nid, SourceProp, Storage.Records.PropertyValue.FromString($"doc {i}"));
                ids.Add(nid.Sequence); // vector binding キーは slot Sequence
            }
            tx.Commit();
        }

        var provider = new MockEmbeddingProvider(Dim);
        await using var pipeline = MakePipeline(provider);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var worker = pipeline.RunAsync(cts.Token);

        await pipeline.ScanAndEnqueueAsync(new EmbeddingScanSpec
        {
            TargetIndexName = IndexName,
            SourcePropertyName = SourceProp,
            Kind = EntityKind.Vertex,
        }, cts.Token);
        await pipeline.WhenDrainedAsync(cts.Token);

        provider.CallCount.Should().Be(3);
        foreach (var id in ids)
        {
            using var cursor = _vectors.KnnSearch(IndexName, provider.Vectorize(""), k: 100);
            var found = false;
            while (cursor.MoveNext())
            {
                if (cursor.Current.EntityId == id) { found = true; break; }
            }
            found.Should().BeTrue($"vertex {id} should have a vector after scan");
        }

        cts.Cancel();
        await worker;
    }

    [Fact]
    public async Task ScanAndEnqueueAsync_is_idempotent_for_unchanged_content()
    {
        using (var tx = _db.BeginWriteTransaction())
        {
            var nid = tx.CreateVertex("Page");
            tx.SetProperty(nid, SourceProp, Storage.Records.PropertyValue.FromString("same"));
            tx.Commit();
        }

        var provider = new MockEmbeddingProvider(Dim);
        await using var pipeline = MakePipeline(provider);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var worker = pipeline.RunAsync(cts.Token);

        var spec = new EmbeddingScanSpec
        {
            TargetIndexName = IndexName,
            SourcePropertyName = SourceProp,
            Kind = EntityKind.Vertex,
        };
        await pipeline.ScanAndEnqueueAsync(spec, cts.Token);
        await pipeline.WhenDrainedAsync(cts.Token);
        int firstCount = provider.CallCount;

        await pipeline.ScanAndEnqueueAsync(spec, cts.Token);
        await pipeline.WhenDrainedAsync(cts.Token);
        provider.CallCount.Should().Be(firstCount, "content hash unchanged → no re-embed");

        cts.Cancel();
        await worker;
    }

    [Fact]
    public async Task EmbedQueryAsync_uses_provider_directly_and_skips_task_log()
    {
        var provider = new MockEmbeddingProvider(Dim);
        await using var pipeline = MakePipeline(provider);

        var vec = await pipeline.EmbedQueryAsync("query text", default);
        vec.Length.Should().Be(Dim);
        provider.CallCount.Should().Be(1);
        provider.LastPurpose.Should().Be(EmbeddingPurpose.Query);
    }

    private sealed class MockEmbeddingProvider(int dimensions) : IEmbeddingProvider
    {
        public string ProviderId => "mock";
        public int Dimensions { get; } = dimensions;
        public DistanceMetric NativeMetric => DistanceMetric.Cosine;
        public int MaxConcurrency => 1;

        public EmbeddingInputLimits Limits { get; } =
            new(MaxLength: 8192, LengthCountingMode.Utf8Bytes, TruncationPolicy.Tail);

        public EmbeddingProviderCapabilities Capabilities { get; } = new()
        {
            EmojiQuality = EmojiTokenizationQuality.Adequate,
            RecognizesZwjSequences = true,
            RecognizesSkinToneModifiers = true,
            RequiresQueryDocumentDistinction = false,
            SupportsBatchInput = false,
        };

        public int CallCount { get; private set; }
        public EmbeddingPurpose LastPurpose { get; private set; }

        public ValueTask<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct)
        {
            CallCount++;
            LastPurpose = request.Purpose;
            var vec = Vectorize(request.Text);
            return ValueTask.FromResult(new EmbeddingResult(
                vec,
                InputLengthMeasured: request.Text.Length,
                MeasurementUnit: LengthCountingMode.Utf16CodeUnits,
                WasTruncated: false,
                Latency: TimeSpan.Zero,
                CorrelationId: request.CorrelationId));
        }

        public float[] Vectorize(string text)
        {
            // Deterministic hash → dense float vector. Bucket index = hash mod Dim.
            var v = new float[Dimensions];
            foreach (var c in text) v[c % Dimensions] += 1.0f;
            // Normalize so cosine math doesn't explode for short text.
            float norm = 0;
            for (int i = 0; i < Dimensions; i++) norm += v[i] * v[i];
            norm = MathF.Sqrt(norm);
            if (norm > 0) for (int i = 0; i < Dimensions; i++) v[i] /= norm;
            return v;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
