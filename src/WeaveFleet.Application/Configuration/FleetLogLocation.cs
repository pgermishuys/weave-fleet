namespace WeaveFleet.Application.Configuration;

/// <summary>
/// Where Fleet writes its daily log files (<c>{Directory}/{FilePrefix}-yyyy-MM-dd.log</c>, UTC dates), or
/// <see cref="Enabled"/> false when file logging is off. Registered by <c>AddFleetDiagnosticLogging</c>.
/// </summary>
public sealed record FleetLogLocation(bool Enabled, string Directory, string FilePrefix)
{
    public static FleetLogLocation Disabled { get; } = new(false, string.Empty, "fleet");
}
