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

    // ─── Messages ─────────────────────────────────────────────────────────────

    /// <summary>Default page size when fetching messages from a live instance. Default: 10.</summary>
    public int LiveMessagePageSize { get; set; } = 10;

    /// <summary>Default page size when fetching messages from the database (history). Default: 10.</summary>
    public int HistoryMessagePageSize { get; set; } = 10;

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

    // ─── Claude Code ─────────────────────────────────────────────────────────

    /// <summary>Claude Code harness configuration.</summary>
    public ClaudeCodeOptions ClaudeCode { get; set; } = new();
}

/// <summary>Configuration for the Claude Code harness.</summary>
public sealed class ClaudeCodeOptions
{
    /// <summary>Path to the claude binary. Default: "claude" (assumes on PATH).</summary>
    public string BinaryPath { get; set; } = "claude";

    /// <summary>Default model to use. Null = let Claude Code choose.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>Permission mode for tool execution. Default: "bypassPermissions".</summary>
    public string PermissionMode { get; set; } = "bypassPermissions";

    /// <summary>Allowed tools. Empty = use Claude Code defaults.</summary>
    public string[] AllowedTools { get; set; } = [];

    /// <summary>Maximum agentic turns per prompt. Null = no limit.</summary>
    public int? MaxTurns { get; set; }

    /// <summary>Maximum budget in USD per prompt. Null = no limit.</summary>
    public decimal? MaxBudgetUsd { get; set; }

    /// <summary>Timeout in seconds for each prompt process. Default: 300 (5 min).</summary>
    public int ProcessTimeoutSeconds { get; set; } = 300;
}
