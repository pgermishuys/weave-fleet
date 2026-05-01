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

    /// <summary>Path to the SQLite database file. Default: <c>&lt;LocalAppData&gt;/WeaveFleet/fleet.db</c>.</summary>
    public string DatabasePath { get; set; } = Path.Combine(FleetPaths.DefaultAppDataDirectory, "fleet.db");

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
    /// defaults to "fleet-analytics.db" in the same directory as <see cref="DatabasePath"/>.
    /// </summary>
    public string ResolvedAnalyticsDatabasePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(AnalyticsDatabasePath))
                return AnalyticsDatabasePath;

            var dir = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
            return Path.Combine(dir ?? ".", "fleet-analytics.db");
        }
    }

    // ─── Claude Code ─────────────────────────────────────────────────────────

    /// <summary>Claude Code harness configuration.</summary>
    public ClaudeCodeOptions ClaudeCode { get; set; } = new();

    // ─── Auth ──────────────────────────────────────────────────────────────────

    /// <summary>Authentication configuration (Clerk/OIDC).</summary>
    public AuthOptions Auth { get; set; } = new();

    // ─── Cloud ─────────────────────────────────────────────────────────────────

    /// <summary>Cloud-mode configuration.</summary>
    public CloudOptions Cloud { get; set; } = new();

    // ─── Data Protection ──────────────────────────────────────────────────────

    /// <summary>Data Protection key persistence configuration.</summary>
    public DataProtectionOptions DataProtection { get; set; } = new();

    /// <summary>Transactional outbox polling and cleanup configuration.</summary>
    public OutboxOptions Outbox { get; set; } = new();

    /// <summary>NATS event substrate configuration.</summary>
    public NatsOptions Nats { get; set; } = new();
}

/// <summary>Transactional outbox polling and retention configuration.</summary>
public sealed class OutboxOptions
{
    /// <summary>
    /// Dispatcher polling interval in milliseconds while idle.
    /// Default: 1000.
    /// </summary>
    public int PollIntervalMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Dispatcher sleep interval in milliseconds after an empty poll.
    /// Default: 250.
    /// </summary>
    public int EmptyPollSleepMilliseconds { get; set; } = 250;

    /// <summary>
    /// Maximum number of outbox rows claimed per dispatch pass.
    /// Default: 100.
    /// </summary>
    public int DispatchBatchSize { get; set; } = 100;

    /// <summary>
    /// Cleanup scan interval in minutes.
    /// Default: 15.
    /// </summary>
    public int CleanupIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Retention window in hours for already-dispatched rows.
    /// Default: 24.
    /// </summary>
    public int RetentionHours { get; set; } = 24;

    /// <summary>
    /// Maximum number of dispatched rows deleted per cleanup pass.
    /// Default: 500.
    /// </summary>
    public int CleanupBatchSize { get; set; } = 500;
}

/// <summary>Authentication / OIDC configuration.</summary>
public sealed class AuthOptions
{
    /// <summary>Enable cookie + OIDC authentication. Default: false (local mode).</summary>
    public bool Enabled { get; set; }

    /// <summary>OIDC authority URL (e.g. Clerk issuer). Required when <see cref="Enabled"/> is true.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>OIDC client ID. Required when <see cref="Enabled"/> is true.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>OIDC client secret. Should be provided via environment variable.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Callback path for the OIDC redirect. Default: /auth/callback.</summary>
    public string CallbackPath { get; set; } = "/auth/callback";

    /// <summary>Callback path after sign-out. Default: /auth/signed-out.</summary>
    public string SignedOutCallbackPath { get; set; } = "/auth/signed-out";

    /// <summary>Origins allowed for CORS when auth is enabled.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Cookie name for the auth session. Default: .WeaveFleet.Auth.</summary>
    public string CookieName { get; set; } = ".WeaveFleet.Auth";

    /// <summary>Auth cookie expiry in minutes. Default: 1440 (24 h).</summary>
    public int CookieExpirationMinutes { get; set; } = 1440;
}

/// <summary>Cloud-mode configuration.</summary>
public sealed class CloudOptions
{
    /// <summary>Enable cloud mode. Default: false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Root directory under which all user workspace directories are created. Required when <see cref="Enabled"/> is true.</summary>
    public string WorkspaceRoot { get; set; } = string.Empty;
}

/// <summary>ASP.NET Core Data Protection key persistence configuration.</summary>
public sealed class DataProtectionOptions
{
    /// <summary>
    /// File system path where Data Protection XML keys are persisted.
    /// When empty, keys are stored in memory only (not suitable for production).
    /// Default: "" (in-memory only — override in cloud/production config).
    /// </summary>
    public string KeyPath { get; set; } = string.Empty;
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
