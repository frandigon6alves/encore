using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Encore.Core.Telemetry;

public static class EncoreTelemetry
{
    public const string Name = "Encore";

    /// <summary>Custom spans (outbox publish, message processing).</summary>
    public static readonly ActivitySource Source = new(Name);

    /// <summary>Custom business metrics.</summary>
    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> AlbumsEnriched = Meter.CreateCounter<long>("encore.albums.enriched");
    public static readonly Counter<long> AlbumImportsFailed = Meter.CreateCounter<long>("encore.album_imports.failed");
    public static readonly Counter<long> OutboxPublished = Meter.CreateCounter<long>("encore.outbox.published");
    public static readonly Histogram<double> AlbumImportDuration =
        Meter.CreateHistogram<double>("encore.album_import.duration", unit: "ms");

    /// <summary>
    /// Traces, metrics and logs over OpenTelemetry. Exported via OTLP when
    /// OTEL_EXPORTER_OTLP_ENDPOINT is set (docker-compose points it at the Aspire dashboard).
    /// </summary>
    public static IHostApplicationBuilder AddEncoreTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddSource(Name)
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(m => m
                .AddMeter(Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddNpgsqlInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
