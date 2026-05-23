using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Application.Tests.Configuration;

public sealed class LegacyDataMigratorTests
{
    [Fact]
    public void Should_move_legacy_db_to_backup_when_source_exists_and_paths_differ()
    {
        using var tempDirectory = new TemporaryDirectory();
        var legacyDatabasePath = tempDirectory.GetPath("fleet.db");
        var fleetDatabasePath = tempDirectory.GetPath("active-fleet.db");

        File.WriteAllText(legacyDatabasePath, "legacy-db");

        LegacyDataMigrator.BackupLegacyAgentDb(fleetDatabasePath, legacyDatabasePath, NullLogger.Instance);

        File.Exists(legacyDatabasePath).ShouldBeFalse();
        File.Exists(legacyDatabasePath + ".legacy-backup").ShouldBeTrue();
        File.ReadAllText(legacyDatabasePath + ".legacy-backup").ShouldBe("legacy-db");
    }

    [Fact]
    public void Should_skip_when_fleet_db_path_matches_legacy_path()
    {
        using var tempDirectory = new TemporaryDirectory();
        var legacyDatabasePath = tempDirectory.GetPath("fleet.db");

        File.WriteAllText(legacyDatabasePath, "legacy-db");

        LegacyDataMigrator.BackupLegacyAgentDb(legacyDatabasePath, legacyDatabasePath, NullLogger.Instance);

        File.Exists(legacyDatabasePath).ShouldBeTrue();
        File.Exists(legacyDatabasePath + ".legacy-backup").ShouldBeFalse();
    }

    [Fact]
    public void Should_skip_when_source_does_not_exist()
    {
        using var tempDirectory = new TemporaryDirectory();
        var legacyDatabasePath = tempDirectory.GetPath("fleet.db");
        var fleetDatabasePath = tempDirectory.GetPath("active-fleet.db");

        LegacyDataMigrator.BackupLegacyAgentDb(fleetDatabasePath, legacyDatabasePath, NullLogger.Instance);

        File.Exists(legacyDatabasePath).ShouldBeFalse();
        File.Exists(legacyDatabasePath + ".legacy-backup").ShouldBeFalse();
    }

    [Fact]
    public void Should_skip_when_backup_already_exists()
    {
        using var tempDirectory = new TemporaryDirectory();
        var legacyDatabasePath = tempDirectory.GetPath("fleet.db");
        var backupDatabasePath = legacyDatabasePath + ".legacy-backup";
        var fleetDatabasePath = tempDirectory.GetPath("active-fleet.db");

        File.WriteAllText(legacyDatabasePath, "legacy-db");
        File.WriteAllText(backupDatabasePath, "existing-backup");

        LegacyDataMigrator.BackupLegacyAgentDb(fleetDatabasePath, legacyDatabasePath, NullLogger.Instance);

        File.Exists(legacyDatabasePath).ShouldBeTrue();
        File.ReadAllText(legacyDatabasePath).ShouldBe("legacy-db");
        File.ReadAllText(backupDatabasePath).ShouldBe("existing-backup");
    }

    [Fact]
    public void Should_copy_wal_and_shm_journal_files_when_present()
    {
        using var tempDirectory = new TemporaryDirectory();
        var legacyDatabasePath = tempDirectory.GetPath("fleet.db");
        var fleetDatabasePath = tempDirectory.GetPath("active-fleet.db");

        File.WriteAllText(legacyDatabasePath, "legacy-db");
        File.WriteAllText(legacyDatabasePath + "-wal", "wal-data");
        File.WriteAllText(legacyDatabasePath + "-shm", "shm-data");

        LegacyDataMigrator.BackupLegacyAgentDb(fleetDatabasePath, legacyDatabasePath, NullLogger.Instance);

        File.Exists(legacyDatabasePath + ".legacy-backup").ShouldBeTrue();
        File.Exists(legacyDatabasePath + ".legacy-backup-wal").ShouldBeTrue();
        File.Exists(legacyDatabasePath + ".legacy-backup-shm").ShouldBeTrue();
        File.ReadAllText(legacyDatabasePath + ".legacy-backup").ShouldBe("legacy-db");
        File.ReadAllText(legacyDatabasePath + ".legacy-backup-wal").ShouldBe("wal-data");
        File.ReadAllText(legacyDatabasePath + ".legacy-backup-shm").ShouldBe("shm-data");
        File.Exists(legacyDatabasePath).ShouldBeFalse();
        File.Exists(legacyDatabasePath + "-wal").ShouldBeFalse();
        File.Exists(legacyDatabasePath + "-shm").ShouldBeFalse();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public string GetPath(string fileName) => Path.Combine(_path, fileName);

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
