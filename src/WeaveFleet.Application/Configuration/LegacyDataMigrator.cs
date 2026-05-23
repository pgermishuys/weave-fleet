using Microsoft.Extensions.Logging;

namespace WeaveFleet.Application.Configuration;

/// <summary>
/// One-shot migration of legacy data files (main DB, analytics DB)
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

        LogScanStart(logger, appData, Environment.CurrentDirectory, null);

        var migrated = 0;

        if (PathsEqual(options.DatabasePath, dbTarget))
        {
            migrated += SafeMigrate(() => TryMoveDb("fleet.db", dbTarget, logger), "fleet.db", logger);
            migrated += SafeMigrate(() => TryMoveDb("weave-fleet.db", dbTarget, logger), "weave-fleet.db", logger);
        }
        else
        {
            LogSettingOverridden(logger, "Fleet:DatabasePath", options.DatabasePath, null);
        }

        if (PathsEqual(options.ResolvedAnalyticsDatabasePath, analyticsTarget))
        {
            migrated += SafeMigrate(() => TryMoveDb("fleet-analytics.db", analyticsTarget, logger), "fleet-analytics.db", logger);
            migrated += SafeMigrate(() => TryMoveDb("weave-fleet-analytics.db", analyticsTarget, logger), "weave-fleet-analytics.db", logger);
        }
        else
        {
            LogSettingOverridden(logger, "Fleet:AnalyticsDatabasePath", options.ResolvedAnalyticsDatabasePath, null);
        }

        LogScanComplete(logger, migrated, null);
    }

    public static void BackupLegacyAgentDb(string fleetDatabasePath, ILogger logger) =>
        BackupLegacyAgentDb(fleetDatabasePath, GetLegacyAgentDatabasePath(), logger);

    internal static void BackupLegacyAgentDb(string fleetDatabasePath, string legacyDatabasePath, ILogger logger)
    {
        legacyDatabasePath = Path.GetFullPath(legacyDatabasePath);
        var backupDatabasePath = legacyDatabasePath + ".legacy-backup";

        if (PathsEqual(fleetDatabasePath, legacyDatabasePath))
        {
            LogLegacyAgentBackupSkippedSamePath(logger, legacyDatabasePath, fleetDatabasePath, null);
            return;
        }

        if (!File.Exists(legacyDatabasePath))
        {
            LogLegacyAgentBackupSourceNotFound(logger, legacyDatabasePath, null);
            return;
        }

        if (File.Exists(backupDatabasePath))
        {
            LogLegacyAgentBackupTargetExists(logger, legacyDatabasePath, backupDatabasePath, null);
            return;
        }

        File.Copy(legacyDatabasePath, backupDatabasePath);
        TryCopyJournal(legacyDatabasePath, backupDatabasePath, "-wal");
        TryCopyJournal(legacyDatabasePath, backupDatabasePath, "-shm");
        File.Delete(legacyDatabasePath);
        TryDelete(legacyDatabasePath + "-wal");
        TryDelete(legacyDatabasePath + "-shm");
        LogLegacyAgentBackupCreated(logger, legacyDatabasePath, backupDatabasePath, null);
    }

    private static int SafeMigrate(Func<bool> migrate, string source, ILogger logger)
    {
        try { return migrate() ? 1 : 0; }
        catch (Exception ex) { LogMigrationFailed(logger, source, ex); return 0; }
    }

    private static string GetLegacyAgentDatabasePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.GetFullPath(Path.Combine(userProfile, ".weave", "fleet.db"));
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool TryMoveDb(string legacyName, string target, ILogger logger)
    {
        var legacy = Path.GetFullPath(legacyName);
        if (!File.Exists(legacy))
        {
            LogCandidateNotFound(logger, legacy, null);
            return false;
        }
        if (File.Exists(target))
        {
            LogTargetExists(logger, legacy, target, null);
            return false;
        }

        // Use copy+delete instead of File.Move so the migration works across
        // volumes (e.g. project on D:, LocalAppData on C: on Windows).
        File.Copy(legacy, target);
        TryCopyJournal(legacy, target, "-wal");
        TryCopyJournal(legacy, target, "-shm");
        File.Delete(legacy);
        TryDelete(legacy + "-wal");
        TryDelete(legacy + "-shm");
        LogMigratedFile(logger, legacy, target, null);
        return true;
    }

    private static void TryCopyJournal(string legacy, string target, string suffix)
    {
        var source = legacy + suffix;
        if (File.Exists(source)) File.Copy(source, target + suffix);
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static readonly Action<ILogger, string, string, Exception?> LogScanStart =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1, "LegacyMigrationScanStart"),
            "Legacy data migration: scanning for relocatable data. Target: {AppData}. CWD: {Cwd}.");

    private static readonly Action<ILogger, int, Exception?> LogScanComplete =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(2, "LegacyMigrationScanComplete"),
            "Legacy data migration: scan complete. {Count} item(s) relocated.");

    private static readonly Action<ILogger, string, string, Exception?> LogSettingOverridden =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(3, "LegacyMigrationSettingOverridden"),
            "Legacy data migration: {Setting} is overridden ({Path}); skipping migration for this path.");

    private static readonly Action<ILogger, string, Exception?> LogCandidateNotFound =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(4, "LegacyMigrationCandidateNotFound"),
            "Legacy data migration: candidate {Source} not present, nothing to migrate.");

    private static readonly Action<ILogger, string, string, Exception?> LogTargetExists =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(5, "LegacyMigrationTargetExists"),
            "Legacy data migration: legacy data found at {Source} but target {Target} already exists. Skipping to avoid clobbering newer data — to adopt the legacy data, stop the app, delete the target, and restart.");

    private static readonly Action<ILogger, string, string, Exception?> LogMigratedFile =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(6, "LegacyDbMigrated"),
            "Legacy data migration: migrated file {Legacy} -> {Target}.");

    private static readonly Action<ILogger, string, Exception?> LogMigrationFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(8, "LegacyMigrationFailed"),
            "Legacy data migration: failed to migrate {Source}; leaving in place.");

    private static readonly Action<ILogger, string, Exception?> LogLegacyAgentBackupSourceNotFound =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(9, "LegacyAgentBackupSourceNotFound"),
            "Legacy agent DB backup: source {Source} not present, nothing to back up.");

    private static readonly Action<ILogger, string, string, Exception?> LogLegacyAgentBackupSkippedSamePath =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(10, "LegacyAgentBackupSkippedSamePath"),
            "Legacy agent DB backup: legacy path {LegacyDatabasePath} matches fleet database path {FleetDatabasePath}; skipping backup.");

    private static readonly Action<ILogger, string, string, Exception?> LogLegacyAgentBackupTargetExists =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(11, "LegacyAgentBackupTargetExists"),
            "Legacy agent DB backup: source {Source} was found but backup target {Target} already exists. Skipping to avoid overwriting the existing backup.");

    private static readonly Action<ILogger, string, string, Exception?> LogLegacyAgentBackupCreated =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(12, "LegacyAgentBackupCreated"),
            "Legacy agent DB backup: moved {Source} -> {Target} using copy and delete.");
}
