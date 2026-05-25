// Quiver.Samples.Observability — OpenTelemetry instrumentation を tracer / meter で有効化し、
// 簡単な書き込み / 読み取りトラフィックを生成して span + metrics をコンソールに流すサンプル。
//
// 実行: dotnet run --project samples/Quiver.Samples.Observability
//
// 外部の Jaeger / Prometheus に流したい場合は AddConsoleExporter() を
// AddOtlpExporter(...) (OTLP gRPC) や AddPrometheusHttpListener() に差し替える。

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Quiver;
using Quiver.Core;
using Quiver.OpenTelemetry;
using Quiver.Stores;

var resource = ResourceBuilder.CreateDefault()
    .AddService(serviceName: "quiver-sample-observability", serviceVersion: "1.0.0");

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource)
    .AddQuiverInstrumentation()       // OB-1: 4 つの ActivitySource を登録。
    .AddConsoleExporter()
    .Build();

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddQuiverInstrumentation()       // OB-1: "Quiver" Meter を登録。
    .AddConsoleExporter((_, readerOpts) =>
        readerOpts.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 1000)
    .Build();

var dbDir = Path.Combine(Path.GetTempPath(), "quiver-otel-sample-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dbDir);

try
{
    using var db = GraphDatabase.Open(dbDir);

    // 100 トランザクションを回して tx.commit / wal.flush / buffer-pool / query を計装出力。
    for (int i = 0; i < 100; i++)
    {
        using var tx = db.BeginTransaction();
        var alice = tx.CreateNode("Person");
        var bob = tx.CreateNode("Person");
        tx.SetProperty(alice, "name", PropertyValue.FromString($"alice-{i}"));
        tx.SetProperty(bob, "name", PropertyValue.FromString($"bob-{i}"));
        tx.CreateRelationship(alice, bob, "KNOWS");
        tx.Commit();
    }

    // 1 回くらい rollback も実行してみる (tx.abort span / counter を確認)。
    try
    {
        using var tx = db.BeginTransaction();
        tx.CreateNode("Temp");
        tx.Rollback();
    }
    catch { /* ignore */ }

    Console.WriteLine("[sample] 100 commits + 1 abort done; flushing OTel exports ...");

    // メトリクスのコンソール出力が走る時間を確保。
    Thread.Sleep(2000);

    var stats = db.Diagnostics.GetStatistics();
    Console.WriteLine($"[sample] final stats: nodes={stats.NodeCount}, rels={stats.RelationshipCount}");
}
finally
{
    try { Directory.Delete(dbDir, recursive: true); } catch { }
}
