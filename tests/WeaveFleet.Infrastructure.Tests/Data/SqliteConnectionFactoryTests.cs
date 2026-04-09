using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Data;

namespace WeaveFleet.Infrastructure.Tests.Data;

public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _dbPath;

    public SqliteConnectionFactoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"weave-test-{Guid.NewGuid()}.db");
    }

    [Fact]
    public void CreateConnection_ReturnsOpenConnection()
    {
        var options = new FleetOptions { DatabasePath = _dbPath };
        var factory = new SqliteConnectionFactory(options);

        using var conn = factory.CreateConnection();

        conn.ShouldNotBeNull();
        conn.State.ShouldBe(System.Data.ConnectionState.Open);
    }

    [Fact]
    public void CreateConnection_AppliesPragmas()
    {
        var options = new FleetOptions { DatabasePath = _dbPath };
        var factory = new SqliteConnectionFactory(options);

        using var conn = (SqliteConnection)factory.CreateConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "PRAGMA foreign_keys";
        var result = cmd.ExecuteScalar();
        result.ShouldBe(1L);
    }

    [Fact]
    public void CreateConnection_EnsuresDirectoryExists()
    {
        var subDir = Path.Combine(Path.GetTempPath(), $"weave-test-dir-{Guid.NewGuid()}");
        var dbPath = Path.Combine(subDir, "test.db");
        var options = new FleetOptions { DatabasePath = dbPath };

        var factory = new SqliteConnectionFactory(options);
        using var conn = factory.CreateConnection();

        Directory.Exists(subDir).ShouldBeTrue();
        conn.Close();
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath)) File.Delete(dbPath);
        if (Directory.Exists(subDir)) Directory.Delete(subDir, recursive: true);
    }

    public void Dispose()
    {
        // Clear SQLite connection pool to release file locks before deletion
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        var wal = _dbPath + "-wal";
        var shm = _dbPath + "-shm";
        if (File.Exists(wal)) File.Delete(wal);
        if (File.Exists(shm)) File.Delete(shm);
    }
}
