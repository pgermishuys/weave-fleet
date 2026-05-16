using Microsoft.Extensions.Logging;

namespace WeaveFleet.Application.Configuration;

/// <summary>
/// Configuration options for persistent diagnostic file logging.
/// Mapped from the <c>Fleet:DiagnosticLogging</c> configuration section.
/// </summary>
public sealed class DiagnosticLoggingOptions
{
    public const string SectionName = "Fleet:DiagnosticLogging";

    /// <summary>
    /// Whether file logging is enabled. Defaults to <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Directory where log files are written.
    /// Defaults to <c>{FleetPaths.DefaultAppDataDirectory}/logs</c>.
    /// </summary>
    public string LogDirectory { get; set; } = Path.Combine(FleetPaths.DefaultAppDataDirectory, "logs");

    /// <summary>
    /// Minimum log level written to file.
    /// Defaults to <see cref="LogLevel.Debug"/>.
    /// Overridden per-environment in <c>appsettings.{env}.json</c>.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    /// <summary>
    /// Number of days to retain log files before automatic cleanup.
    /// Defaults to 30.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Prefix used for log file names. Files are named <c>{FilePrefix}-yyyy-MM-dd.log</c>.
    /// Defaults to <c>fleet</c>.
    /// </summary>
    public string FilePrefix { get; set; } = "fleet";
}
