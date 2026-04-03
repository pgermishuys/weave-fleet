using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Data;
using WeaveFleet.Infrastructure.Data;

namespace WeaveFleet.Infrastructure.Tests.Data;

/// <summary>
/// Helpers for setting up in-memory SQLite databases for tests.
/// </summary>
internal static class TestDbHelper
{
    /// <summary>
    /// Creates an open in-memory SQLite connection with migrations applied.
    /// The caller is responsible for disposing the connection.
    /// </summary>
    public static async Task<SqliteConnection> CreateMigratedConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        // Apply pragmas
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            """;
        cmd.ExecuteNonQuery();

        // Wrap the in-memory connection in an adapter for MigrationRunner
        var factory = new InMemoryConnectionFactory(connection);
        var runner = new MigrationRunner(factory, NullLogger<MigrationRunner>.Instance);
        await runner.ApplyMigrationsAsync(connection);

        return connection;
    }

    /// <summary>
    /// Creates a named shared-cache in-memory database factory. The keeper connection must be kept
    /// open for the database to persist. Each call to CreateConnection() opens a new connection to
    /// the same database, allowing the repositories to open/close connections safely.
    /// </summary>
    public static async Task<(SqliteConnection Keeper, IDbConnectionFactory Factory)> CreateSharedDbAsync()
    {
        // Ensure Dapper global settings are configured
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        var dbName = Guid.NewGuid().ToString("N");
        var connStr = $"Data Source={dbName};Mode=Memory;Cache=Shared";

        // Keeper: stays open for the test duration to prevent database destruction
        var keeper = new SqliteConnection(connStr);
        keeper.Open();

        using var cmd = keeper.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();

        // Apply migrations via a temporary connection
        var factory = new SharedCacheFactory(connStr);
        using var migConn = new SqliteConnection(connStr);
        migConn.Open();
        var runner = new MigrationRunner(factory, NullLogger<MigrationRunner>.Instance);
        await runner.ApplyMigrationsAsync(migConn);

        return (keeper, factory);
    }

    /// <summary>
    /// Adapter that returns the same already-open in-memory connection.
    /// </summary>
    private sealed class InMemoryConnectionFactory(SqliteConnection connection) : IDbConnectionFactory
    {
        public System.Data.IDbConnection CreateConnection() => connection;
    }

    /// <summary>
    /// Factory that creates new connections to the same named shared-cache in-memory database.
    /// Each connection is opened immediately so Dapper does not need to open it.
    /// </summary>
    internal sealed class SharedCacheFactory(string connectionString) : IDbConnectionFactory
    {
        public System.Data.IDbConnection CreateConnection()
        {
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys=ON;";
            cmd.ExecuteNonQuery();
            return conn;
        }
    }
}
