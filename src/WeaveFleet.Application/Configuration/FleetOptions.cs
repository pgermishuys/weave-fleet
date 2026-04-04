namespace WeaveFleet.Application.Configuration;

/// <summary>
/// Configuration options for the Weave Fleet application.
/// Bound from the "Fleet" section in appsettings.json.
/// </summary>
public sealed class FleetOptions
{
    public const string SectionName = "Fleet";

    /// <summary>TCP port the Kestrel server listens on. Default: 3000.</summary>
    public int Port { get; set; } = 3000;

    /// <summary>Host/IP address Kestrel binds to. Default: 127.0.0.1.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Path to the SQLite database file. Default: weave-fleet.db in current directory.</summary>
    public string DatabasePath { get; set; } = "weave-fleet.db";

    /// <summary>Enable verbose debug logging and developer-friendly error responses.</summary>
    public bool Debug { get; set; }

    /// <summary>Computed listen URL from Host and Port.</summary>
    public string ListenUrl => $"http://{Host}:{Port}";

    /// <summary>Start of the port range used for harness processes. Default: 10000.</summary>
    public int HarnessPortRangeStart { get; set; } = 10000;

    /// <summary>End of the port range used for harness processes (inclusive). Default: 10999.</summary>
    public int HarnessPortRangeEnd { get; set; } = 10999;

    /// <summary>Seconds to wait for a harness process to signal readiness. Default: 30.</summary>
    public int HarnessStartupTimeoutSeconds { get; set; } = 30;

    /// <summary>Seconds to wait for a harness process to exit gracefully before force-killing. Default: 10.</summary>
    public int HarnessShutdownTimeoutSeconds { get; set; } = 10;
}
