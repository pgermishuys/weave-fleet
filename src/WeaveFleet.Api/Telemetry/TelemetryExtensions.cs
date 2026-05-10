using System.Diagnostics.CodeAnalysis;
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
    [RequiresUnreferencedCode("TelemetryOptions binding uses reflection-based IConfiguration.Get<T>; all config properties are primitive types and safe at runtime.")]
    public static IHostApplicationBuilder AddFleetTelemetry(this IHostApplicationBuilder builder)
    {
        var telemetryOptions = builder.Configuration
            .GetSection(TelemetryOptions.SectionName)
            .Get<TelemetryOptions>() ?? new TelemetryOptions();

        // Only register OTLP exporters when a collector endpoint is explicitly configured
        // (via OTEL_EXPORTER_OTLP_ENDPOINT env var or Fleet:Telemetry:OtlpEndpoint in config).
        var otlpEndpointEnv = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var hasExplicitEndpoint = !string.IsNullOrWhiteSpace(otlpEndpointEnv)
            || !string.IsNullOrWhiteSpace(telemetryOptions.OtlpEndpoint);
        if (!hasExplicitEndpoint)
        {
            Console.WriteLine("[Fleet:Telemetry] OTLP disabled — no OTEL_EXPORTER_OTLP_ENDPOINT or Fleet:Telemetry:OtlpEndpoint configured.");
            return builder;
        }

        var baseEndpoint = (!string.IsNullOrWhiteSpace(otlpEndpointEnv)
            ? otlpEndpointEnv
            : telemetryOptions.OtlpEndpoint).TrimEnd('/');

        Console.WriteLine($"[Fleet:Telemetry] OTLP enabled — exporting to {baseEndpoint}");

        var endpoint = new Uri(baseEndpoint);

        // Resource: service name + version (used by the logging pipeline)
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: FleetInstrumentation.ServiceName,
                serviceVersion: FleetInstrumentation.ServiceVersion);

        // Aspire Dashboard OTLP endpoint uses gRPC.
        // gRPC exporters use the base endpoint directly (no per-signal path suffixes).

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
                        otlp.Endpoint = endpoint;
                        otlp.Protocol = OtlpExportProtocol.Grpc;
                        otlp.TimeoutMilliseconds = telemetryOptions.ExportTimeoutMilliseconds;
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
                        otlp.Endpoint = endpoint;
                        otlp.Protocol = OtlpExportProtocol.Grpc;
                        otlp.TimeoutMilliseconds = telemetryOptions.ExportTimeoutMilliseconds;
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
                otlp.Endpoint = endpoint;
                otlp.Protocol = OtlpExportProtocol.Grpc;
                otlp.TimeoutMilliseconds = telemetryOptions.ExportTimeoutMilliseconds;
            });
        });

        return builder;
    }
}
