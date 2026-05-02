namespace WeaveFleet.Application.Configuration;

/// <summary>
/// Configuration options for OpenTelemetry.
/// Bound from the "Fleet:Telemetry" section in appsettings.json.
/// Standard OTEL environment variables (OTEL_EXPORTER_OTLP_ENDPOINT, etc.) take precedence.
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Fleet:Telemetry";

    /// <summary>Enable OpenTelemetry export. When false, OTEL SDK is not registered. Default: false.</summary>
    public bool Enabled { get; set; }

    /// <summary>OTLP collector endpoint. Default: http://localhost:4317 (gRPC). Override with OTEL_EXPORTER_OTLP_ENDPOINT env var.</summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";

    /// <summary>Timeout in milliseconds for OTLP export requests. Keeps shutdown fast when no collector is running. Default: 1000.</summary>
    public int ExportTimeoutMilliseconds { get; set; } = 1000;

}
