using Azure.Monitor.OpenTelemetry.AspNetCore;
using FieldTrack.Api.Ingest;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FieldTrack.Api.Observability;

/// <summary>
/// Metrics, traces and logs through OpenTelemetry with direct exporters and no Collector (spec #21, #48): the
/// Azure Monitor Distro when the Application Insights connection string is set, otherwise OTLP when an endpoint is
/// set (the local <c>grafana/otel-lgtm</c> container). With neither, instruments still run and nothing leaves the process.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>Configuration key that turns on the Azure Monitor Distro; the Distro itself reads the same key.</summary>
    public const string AzureConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    /// <summary>Configuration key that turns on the OTLP exporters; they read the same key and <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>.</summary>
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>Registers OpenTelemetry for the API host and its ingest worker.</summary>
    public static WebApplicationBuilder AddFieldTrackObservability(this WebApplicationBuilder builder)
    {
        var telemetry = builder.Services.AddOpenTelemetry()
            // OTEL_SERVICE_NAME overrides the name.
            .ConfigureResource(resource => resource.AddService("fieldtrack-api"))
            // Npgsql 10 emits OpenTelemetry semantic-convention spans and metrics on its own source and meter.
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(IngestWorker.MeterName, "Npgsql"))
            // #23: no tokens in telemetry. The instrumentation redacts every query-string value in its spans, which
            // keeps SignalR's access_token out (ObservabilityTests); do not set the option that turns that off.
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddSource(IngestWorker.ActivitySourceName, "Npgsql"))
            .WithLogging();

        if (!string.IsNullOrEmpty(builder.Configuration[AzureConnectionStringKey]))
        {
            telemetry.UseAzureMonitor();
        }
        else if (!string.IsNullOrEmpty(builder.Configuration[OtlpEndpointKey]))
        {
            telemetry.UseOtlpExporter();
        }

        return builder;
    }
}
