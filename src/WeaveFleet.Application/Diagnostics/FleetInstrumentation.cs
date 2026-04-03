using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace WeaveFleet.Application.Diagnostics;

/// <summary>
/// Central instrumentation constants and singletons for OpenTelemetry.
/// Holds the <see cref="ActivitySource"/> for distributed tracing and
/// <see cref="Meter"/> for custom metrics.
/// </summary>
public static class FleetInstrumentation
{
    /// <summary>Service name used in OTEL resource attributes.</summary>
    public const string ServiceName = "weave-fleet";

    /// <summary>Service version, derived from the assembly informational version.</summary>
    public static readonly string ServiceVersion =
        typeof(FleetInstrumentation).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

    /// <summary>ActivitySource for creating distributed trace spans.</summary>
    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    /// <summary>Meter for recording custom metrics.</summary>
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);
}
