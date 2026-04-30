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

        if (PathsEqual(options.Nats.DataDirectory, natsTarget))
        {
            migrated += SafeMigrate(
                () => TryMoveDirectory(Path.Combine("data", "nats"), natsTarget, logger),
                "data/nats", logger);
        }
        else
        {
            LogSettingOverridden(logger, "Fleet:Nats:DataDirectory", options.Nats.DataDirectory, null);
        }

        LogScanComplete(logger, migrated, null);
    }

    private static int SafeMigrate(Func<bool> migrate, string source, ILogger logger)
    {
        try { return migrate() ? 1 : 0; }
        catch (Exception ex) { LogMigrationFailed(logger, source, ex); return 0; }
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

    private static bool TryMoveDirectory(string legacyDir, string target, ILogger logger)
    {
        var legacy = Path.GetFullPath(legacyDir);
        if (!Directory.Exists(legacy))
        {
            LogCandidateNotFound(logger, legacy, null);
            return false;
        }
        if (Directory.Exists(target))
        {
            LogTargetExists(logger, legacy, target, null);
            return false;
        }

        // Recursive copy+delete instead of Directory.Move so the migration works
        // across volumes (Directory.Move fails with IOException across drives).
        CopyDirectoryRecursive(legacy, target);
        Directory.Delete(legacy, recursive: true);
        LogMigratedDirectory(logger, legacy, target, null);
        return true;
    }

    private static void CopyDirectoryRecursive(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(target, Path.GetFileName(dir)));
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

    private static readonly Action<ILogger, string, string, Exception?> LogMigratedDirectory =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(7, "LegacyDirMigrated"),
            "Legacy data migration: migrated directory {Legacy} -> {Target}.");

    private static readonly Action<ILogger, string, Exception?> LogMigrationFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(8, "LegacyMigrationFailed"),
            "Legacy data migration: failed to migrate {Source}; leaving in place.");
}
