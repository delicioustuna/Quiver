using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Quiver.Telemetry;

namespace Quiver.OpenTelemetry;

/// <summary>
/// OB-1: Quiver の <see cref="System.Diagnostics.ActivitySource"/> / <see cref="System.Diagnostics.Metrics.Meter"/>
/// を OpenTelemetry の TracerProvider / MeterProvider に登録するためのヘルパ。
/// </summary>
/// <example>
/// <code>
/// using var tracer = Sdk.CreateTracerProviderBuilder()
///     .AddQuiverInstrumentation()
///     .AddConsoleExporter()
///     .Build();
///
/// using var meter = Sdk.CreateMeterProviderBuilder()
///     .AddQuiverInstrumentation()
///     .AddPrometheusHttpListener()
///     .Build();
/// </code>
/// </example>
public static class QuiverInstrumentation
{
    /// <summary>
    /// Quiver が発行する全 <see cref="System.Diagnostics.ActivitySource"/>
    /// (<c>Quiver.Transaction</c>, <c>Quiver.Query</c>, <c>Quiver.Checkpoint</c>, <c>Quiver.WalFlush</c>)
    /// を <paramref name="builder"/> に登録する。
    /// </summary>
    public static TracerProviderBuilder AddQuiverInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var source in QuiverTelemetry.AllSourceNames)
            builder.AddSource(source);
        return builder;
    }

    /// <summary>
    /// Quiver が発行する <see cref="System.Diagnostics.Metrics.Meter"/> (<c>Quiver</c>)
    /// を <paramref name="builder"/> に登録する。
    /// </summary>
    public static MeterProviderBuilder AddQuiverInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddMeter(QuiverTelemetry.MeterName);
        return builder;
    }
}
