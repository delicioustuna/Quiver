using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Yatagarasu.Telemetry;

namespace Yatagarasu.OpenTelemetry;

/// <summary>
/// Yatagarasu の <see cref="System.Diagnostics.ActivitySource"/> / <see cref="System.Diagnostics.Metrics.Meter"/>
/// を OpenTelemetry の TracerProvider / MeterProvider に登録するためのヘルパ。
/// </summary>
/// <example>
/// <code>
/// using var tracer = Sdk.CreateTracerProviderBuilder()
///     .AddYatagarasuInstrumentation()
///     .AddConsoleExporter()
///     .Build();
///
/// using var meter = Sdk.CreateMeterProviderBuilder()
///     .AddYatagarasuInstrumentation()
///     .AddPrometheusHttpListener()
///     .Build();
/// </code>
/// </example>
public static class YatagarasuInstrumentation
{
    /// <summary>
    /// Yatagarasu が発行する全 <see cref="System.Diagnostics.ActivitySource"/>
    /// (<c>Yatagarasu.Transaction</c>, <c>Yatagarasu.Query</c>, <c>Yatagarasu.Checkpoint</c>, <c>Yatagarasu.WalFlush</c>)
    /// を <paramref name="builder"/> に登録する。
    /// </summary>
    public static TracerProviderBuilder AddYatagarasuInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var source in YatagarasuTelemetry.AllSourceNames)
            builder.AddSource(source);
        return builder;
    }

    /// <summary>
    /// Yatagarasu が発行する <see cref="System.Diagnostics.Metrics.Meter"/> (<c>Yatagarasu</c>)
    /// を <paramref name="builder"/> に登録する。
    /// </summary>
    public static MeterProviderBuilder AddYatagarasuInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddMeter(YatagarasuTelemetry.MeterName);
        return builder;
    }
}
