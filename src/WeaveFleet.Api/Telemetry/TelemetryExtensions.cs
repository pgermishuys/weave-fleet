using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;

namespace WeaveFleet.Api.Telemetry;

/// <summary>
/// Extension methods for registering OpenTelemetry services (traces, metrics, logs).
/// </summary>
public static class TelemetryExtensions
{
    /// <summary>
    /// Adds OpenTelemetry tracing, metrics, and logging to the host.
    /// Reads configuration from the "Fleet:Telemetry" section and standard OTEL environment variables.
    /// </summary>
    public static IHostApplicationBuilder AddFleetTelemetry(this IHostApplicationBuilder builder)
    {
        var telemetryOptions = builder.Configuration
            .GetSection(TelemetryOptions.SectionName)
            .Get<TelemetryOptions>() ?? new TelemetryOptions();

        if (!telemetryOptions.Enabled)
        {
            return builder;
        }

        // Resource: service name + version (used by the logging pipeline)
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: FleetInstrumentation.ServiceName,
                serviceVersion: FleetInstrumentation.ServiceVersion);

        // Per-signal AddOtlpExporter does NOT auto-append /v1/{signal} paths when
        // Endpoint is explicitly set, so we build the full URL for each signal.
        var baseEndpoint = telemetryOptions.OtlpEndpoint.TrimEnd('/');

        // --- Tracing & Metrics ---
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                FleetInstrumentation.ServiceName,
                serviceVersion: FleetInstrumentation.ServiceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(FleetInstrumentation.ServiceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = new Uri($"{baseEndpoint}/v1/traces");
                        otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    });

            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(FleetInstrumentation.ServiceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = new Uri($"{baseEndpoint}/v1/metrics");
                        otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
                    });
            });

        // --- Logging ---
        // Add OTEL logging provider ALONGSIDE the existing Console provider.
        // WebApplication.CreateBuilder configures Console by default; we add OTEL on top.
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(resourceBuilder);
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            logging.AddOtlpExporter(otlp =>
            {
                otlp.Endpoint = new Uri($"{baseEndpoint}/v1/logs");
                otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
            });
        });

        return builder;
    }
}
