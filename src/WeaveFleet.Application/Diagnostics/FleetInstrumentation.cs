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

    /// <summary>Commit SHA parsed from the assembly informational version when available.</summary>
    public static readonly string ServiceCommit = GetServiceCommit(ServiceVersion);

    /// <summary>ActivitySource for creating distributed trace spans.</summary>
    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    /// <summary>Meter for recording custom metrics.</summary>
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    // ─── Analytics metrics ────────────────────────────────────────────────────

    /// <summary>Total tokens consumed across all sessions.</summary>
    public static readonly Counter<long> TokensConsumed =
        Meter.CreateCounter<long>("weave_fleet.tokens.consumed", "tokens",
            "Total tokens consumed across all sessions");

    /// <summary>Total actual cost incurred across all sessions.</summary>
    public static readonly Counter<double> CostIncurred =
        Meter.CreateCounter<double>("weave_fleet.cost.incurred", "USD",
            "Total cost incurred across all sessions");

    /// <summary>Total estimated cost across all sessions.</summary>
    public static readonly Counter<double> EstimatedCostIncurred =
        Meter.CreateCounter<double>("weave_fleet.cost.estimated", "USD",
            "Total estimated cost across all sessions");

    /// <summary>Total AI messages processed.</summary>
    public static readonly Counter<long> MessagesProcessed =
        Meter.CreateCounter<long>("weave_fleet.messages.processed", "messages",
            "Total AI messages processed");

    /// <summary>Distribution of per-message costs.</summary>
    public static readonly Histogram<double> MessageCost =
        Meter.CreateHistogram<double>("weave_fleet.message.cost", "USD",
            "Distribution of per-message costs");

    /// <summary>Distribution of per-message token counts.</summary>
    public static readonly Histogram<long> MessageTokens =
        Meter.CreateHistogram<long>("weave_fleet.message.tokens", "tokens",
            "Distribution of per-message token counts");

    private static string GetServiceCommit(string serviceVersion)
    {
        var separatorIndex = serviceVersion.IndexOf('+', StringComparison.Ordinal);
        if (separatorIndex < 0 || separatorIndex == serviceVersion.Length - 1)
            return "unknown";

        return serviceVersion[(separatorIndex + 1)..];
    }
}
