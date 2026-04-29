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
            SafeMigrate(() => TryMoveDb("fleet.db", dbTarget, logger), "fleet.db", logger);
            SafeMigrate(() => TryMoveDb("weave-fleet.db", dbTarget, logger), "weave-fleet.db", logger);
        }

        if (PathsEqual(options.ResolvedAnalyticsDatabasePath, analyticsTarget))
        {
            SafeMigrate(() => TryMoveDb("fleet-analytics.db", analyticsTarget, logger), "fleet-analytics.db", logger);
            SafeMigrate(() => TryMoveDb("weave-fleet-analytics.db", analyticsTarget, logger), "weave-fleet-analytics.db", logger);
        }

        if (PathsEqual(options.Nats.DataDirectory, natsTarget))
        {
            SafeMigrate(
                () => TryMoveDirectory(Path.Combine("data", "nats"), natsTarget, logger),
                "data/nats", logger);
        }
    }

    private static void SafeMigrate(Action migrate, string source, ILogger logger)
    {
        try { migrate(); }
        catch (Exception ex) { LogMigrationFailed(logger, source, ex); }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void TryMoveDb(string legacyName, string target, ILogger logger)
    {
        var legacy = Path.GetFullPath(legacyName);
        if (!File.Exists(legacy)) return;
        if (File.Exists(target)) return;

        // Use copy+delete instead of File.Move so the migration works across
        // volumes (e.g. project on D:, LocalAppData on C: on Windows).
        File.Copy(legacy, target);
        TryCopyJournal(legacy, target, "-wal");
        TryCopyJournal(legacy, target, "-shm");
        File.Delete(legacy);
        TryDelete(legacy + "-wal");
        TryDelete(legacy + "-shm");
        LogMigratedFile(logger, legacy, target, null);
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

    private static void TryMoveDirectory(string legacyDir, string target, ILogger logger)
    {
        var legacy = Path.GetFullPath(legacyDir);
        if (!Directory.Exists(legacy)) return;
        if (Directory.Exists(target)) return;

        // Recursive copy+delete instead of Directory.Move so the migration works
        // across volumes (Directory.Move fails with IOException across drives).
        CopyDirectoryRecursive(legacy, target);
        Directory.Delete(legacy, recursive: true);
        LogMigratedDirectory(logger, legacy, target, null);
    }

    private static void CopyDirectoryRecursive(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private static readonly Action<ILogger, string, string, Exception?> LogMigratedFile =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1, "LegacyDbMigrated"),
            "Migrated legacy data file {Legacy} -> {Target}.");

    private static readonly Action<ILogger, string, string, Exception?> LogMigratedDirectory =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(2, "LegacyDirMigrated"),
            "Migrated legacy data directory {Legacy} -> {Target}.");

    private static readonly Action<ILogger, string, Exception?> LogMigrationFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "LegacyMigrationFailed"),
            "Failed to migrate legacy data {Source} — leaving in place.");
}
