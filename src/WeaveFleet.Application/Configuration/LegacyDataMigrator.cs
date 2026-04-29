using Microsoft.Extensions.Logging;

namespace WeaveFleet.Application.Configuration;

/// <summary>
/// One-shot migration of legacy data files (main DB, analytics DB, NATS JetStream dir)
/// from the process working directory (where pre-LocalAppData defaults wrote them) to
/// the new <see cref="FleetPaths.DefaultAppDataDirectory"/>. Only runs for paths that
/// are still at their hard-coded defaults — any explicit override (env var, appsettings,
/// launcher script) suppresses migration for that path.
/// </summary>
public static class LegacyDataMigrator
{
    public static void MigrateIfNeeded(FleetOptions options, ILogger logger)
    {
        var appData = FleetPaths.DefaultAppDataDirectory;
        Directory.CreateDirectory(appData);

        var dbTarget = Path.Combine(appData, "fleet.db");
        var analyticsTarget = Path.Combine(appData, "fleet-analytics.db");
        var natsTarget = Path.Combine(appData, "nats");

        if (PathsEqual(options.DatabasePath, dbTarget))
        {
            TryMoveDb("fleet.db", dbTarget, logger);
            TryMoveDb("weave-fleet.db", dbTarget, logger);
        }

        if (PathsEqual(options.ResolvedAnalyticsDatabasePath, analyticsTarget))
        {
            TryMoveDb("fleet-analytics.db", analyticsTarget, logger);
            TryMoveDb("weave-fleet-analytics.db", analyticsTarget, logger);
        }

        if (PathsEqual(options.Nats.DataDirectory, natsTarget))
        {
            TryMoveDirectory(Path.Combine("data", "nats"), natsTarget, logger);
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void TryMoveDb(string legacyName, string target, ILogger logger)
    {
        var legacy = Path.GetFullPath(legacyName);
        if (!File.Exists(legacy)) return;
        if (File.Exists(target)) return;

        File.Move(legacy, target);
        TryMoveJournal(legacy, target, "-wal");
        TryMoveJournal(legacy, target, "-shm");
        LogMigratedFile(logger, legacy, target, null);
    }

    private static void TryMoveJournal(string legacy, string target, string suffix)
    {
        var source = legacy + suffix;
        if (File.Exists(source)) File.Move(source, target + suffix);
    }

    private static void TryMoveDirectory(string legacyDir, string target, ILogger logger)
    {
        var legacy = Path.GetFullPath(legacyDir);
        if (!Directory.Exists(legacy)) return;
        if (Directory.Exists(target)) return;

        Directory.Move(legacy, target);
        LogMigratedDirectory(logger, legacy, target, null);
    }

    private static readonly Action<ILogger, string, string, Exception?> LogMigratedFile =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1, "LegacyDbMigrated"),
            "Migrated legacy data file {Legacy} -> {Target}.");

    private static readonly Action<ILogger, string, string, Exception?> LogMigratedDirectory =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(2, "LegacyDirMigrated"),
            "Migrated legacy data directory {Legacy} -> {Target}.");
}
