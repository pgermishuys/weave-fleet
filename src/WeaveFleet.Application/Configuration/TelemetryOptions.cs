namespace WeaveFleet.Application.Configuration;

/// <summary>
/// Configuration options for OpenTelemetry.
/// Bound from the "Fleet:Telemetry" section in appsettings.json.
/// Standard OTEL environment variables (OTEL_EXPORTER_OTLP_ENDPOINT, etc.) take precedence.
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Fleet:Telemetry";

    /// <summary>OTLP collector endpoint. If set (or OTEL_EXPORTER_OTLP_ENDPOINT env var is set), telemetry is enabled.</summary>
    public string OtlpEndpoint { get; set; } = "";

    /// <summary>Timeout in milliseconds for OTLP export requests. Keeps shutdown fast when no collector is running. Default: 1000.</summary>
    public int ExportTimeoutMilliseconds { get; set; } = 1000;

}
