using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Api.Telemetry;

/// <summary>
/// Extension methods for registering Fleet diagnostic file logging.
/// </summary>
internal static class DiagnosticLoggingExtensions
{
    /// <summary>
    /// Adds persistent file logging to the host.
    /// Reads options from <c>Fleet:DiagnosticLogging</c> using manual property reads
    /// (no reflection-based <c>IConfiguration.Get&lt;T&gt;()</c>) to remain AOT/trimming safe.
    /// </summary>
    public static IHostApplicationBuilder AddFleetDiagnosticLogging(
        this IHostApplicationBuilder builder)
    {
        // Manual property reads — avoids IConfiguration.Get<T>() reflection binding.
        var section = builder.Configuration.GetSection(DiagnosticLoggingOptions.SectionName);

        var enabled = !string.Equals(section["Enabled"], "false", StringComparison.OrdinalIgnoreCase);
        if (!enabled)
        {
            Console.WriteLine("[Fleet:DiagnosticLogging] Disabled via configuration.");
            return builder;
        }

        var defaultLogDir = Path.Combine(FleetPaths.DefaultAppDataDirectory, "logs");
        var logDirectory = section["LogDirectory"] is { Length: > 0 } dir ? dir : defaultLogDir;

        var filePrefix = section["FilePrefix"] is { Length: > 0 } prefix ? prefix : "fleet";

        var retentionDays = 30;
        if (section["RetentionDays"] is { Length: > 0 } retentionStr
            && int.TryParse(retentionStr, out var parsed))
        {
            retentionDays = parsed;
        }

        // Minimum level: config overrides the environment default.
        // Development default: Debug. All other environments: Warning.
        LogLevel minimumLevel;
        if (section["MinimumLevel"] is { Length: > 0 } levelStr
            && Enum.TryParse<LogLevel>(levelStr, ignoreCase: true, out var configuredLevel))
        {
            minimumLevel = configuredLevel;
        }
        else
        {
            // Default to Debug while the product is in active development.
            // Tighten to Warning for production once stable.
            minimumLevel = LogLevel.Debug;
        }

        // Ensure the log directory exists before registering the provider.
        Directory.CreateDirectory(logDirectory);

        var provider = new FileLoggerProvider(logDirectory, filePrefix, minimumLevel);
        builder.Logging.AddProvider(provider);

        // Register cleanup service as a hosted service.
        builder.Services.AddHostedService(sp =>
            new LogFileCleanupService(
                logDirectory,
                filePrefix,
                retentionDays,
                sp.GetRequiredService<ILogger<LogFileCleanupService>>()));

        Console.WriteLine($"[Fleet:DiagnosticLogging] Writing logs ({minimumLevel}+) to {logDirectory}");

        return builder;
    }
}
