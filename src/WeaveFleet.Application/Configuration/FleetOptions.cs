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

    // ─── Analytics ────────────────────────────────────────────────────────────

    /// <summary>Path to the analytics SQLite database file. Default: "" (computed alongside DatabasePath).</summary>
    public string AnalyticsDatabasePath { get; set; } = "";

    /// <summary>Enable analytics collection. Default: true.</summary>
    public bool AnalyticsEnabled { get; set; } = true;

    /// <summary>Batch flush interval for analytics writes in seconds. Default: 2.</summary>
    public int AnalyticsFlushIntervalSeconds { get; set; } = 2;

    /// <summary>Maximum batch size for analytics writes. Default: 50.</summary>
    public int AnalyticsMaxBatchSize { get; set; } = 50;

    /// <summary>Rollup computation interval in minutes. Default: 5.</summary>
    public int AnalyticsRollupIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Resolved analytics database path. When <see cref="AnalyticsDatabasePath"/> is empty,
    /// defaults to "weave-fleet-analytics.db" in the same directory as <see cref="DatabasePath"/>.
    /// </summary>
    public string ResolvedAnalyticsDatabasePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(AnalyticsDatabasePath))
                return AnalyticsDatabasePath;

            var dir = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
            return Path.Combine(dir ?? ".", "weave-fleet-analytics.db");
        }
    }
}
