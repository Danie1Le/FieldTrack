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

    /// <summary>
    /// The environment variable (read through configuration, like the instrumentation does) that turns off the
    /// instrumentation's redaction of query-string values in <c>url.query</c>. Startup refuses it: SignalR clients send
    /// their <c>access_token</c> in the query string (#23: no tokens in telemetry).
    /// </summary>
    public const string DisableUrlQueryRedactionKey = "OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION";

    /// <summary>Configuration key that turns the Azure Monitor Live Metrics stream on. It is off unless set, so the host sends nothing beyond the exporter's own traffic.</summary>
    public const string LiveMetricsKey = "AzureMonitor:EnableLiveMetrics";

    /// <summary>
    /// Configuration key for a fixed fraction of traces to keep (0 to 1) in Application Insights (#158). Unset, the Distro's
    /// default keeps at most 5 traces per second, which holds a busy API under the free ingestion allowance; set, the fixed
    /// ratio replaces that rate limit.
    /// </summary>
    public const string SamplingRatioKey = "AzureMonitor:SamplingRatio";

    /// <summary>Registers OpenTelemetry for the API host and its ingest worker.</summary>
    public static WebApplicationBuilder AddFieldTrackObservability(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<UrlQueryRedaction>()
            .Validate<IConfiguration>(
                (_, configuration) => !configuration.GetValue<bool>(DisableUrlQueryRedactionKey),
                $"{DisableUrlQueryRedactionKey} must not be set: it would send SignalR access tokens to telemetry.")
            .ValidateOnStart();

        var telemetry = builder.Services.AddOpenTelemetry()
            // OTEL_SERVICE_NAME overrides the name.
            .ConfigureResource(resource => resource.AddService("fieldtrack-api"))
            // Npgsql 10 emits OpenTelemetry semantic-convention spans and metrics on its own source and meter.
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(IngestMetrics.MeterName, "Npgsql"))
            // #23: no tokens in telemetry. The instrumentation redacts every query-string value in its spans, which
            // keeps SignalR's access_token out (ObservabilityTests); the guard above refuses DisableUrlQueryRedactionKey.
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddSource(IngestWorker.ActivitySourceName, "Npgsql"))
            .WithLogging();

        if (!string.IsNullOrEmpty(builder.Configuration[AzureConnectionStringKey]))
        {
            // The Distro switches the redaction off unless this is set, which would send SignalR's access_token to
            // Application Insights; the guard above refuses a stray "true".
            builder.Configuration[DisableUrlQueryRedactionKey] ??= "false";
            var samplingRatio = builder.Configuration.GetValue<float?>(SamplingRatioKey);
            telemetry.UseAzureMonitor(options =>
            {
                options.EnableLiveMetrics = builder.Configuration.GetValue<bool>(LiveMetricsKey);
                if (samplingRatio is { } ratio)
                {
                    options.SamplingRatio = ratio;
                    options.TracesPerSecond = null;
                }
            });
        }
        else if (!string.IsNullOrEmpty(builder.Configuration[OtlpEndpointKey]))
        {
            telemetry.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>Carries the startup check that the URL query redaction is on; it has no settings.</summary>
    private sealed class UrlQueryRedaction;
}
