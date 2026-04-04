using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Data;
using WeaveFleet.Infrastructure.Data;

namespace WeaveFleet.Infrastructure.Tests.Analytics;

/// <summary>
/// Helpers for setting up in-memory SQLite analytics databases for tests.
/// Mirrors <c>TestDbHelper</c> but uses <see cref="IAnalyticsDbConnectionFactory"/>
/// and applies analytics migrations.
/// </summary>
internal static class AnalyticsTestDbHelper
{
    /// <summary>
    /// Creates a named shared-cache in-memory analytics database with migrations applied.
    /// The keeper connection must be kept open for the database to persist.
    /// Each call to <see cref="IAnalyticsDbConnectionFactory.CreateConnection"/> opens a new
    /// connection to the same database, allowing repositories to open/close connections safely.
    /// </summary>
    public static async Task<(SqliteConnection Keeper, IAnalyticsDbConnectionFactory Factory)> CreateSharedDbAsync()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        var dbName = Guid.NewGuid().ToString("N");
        var connStr = $"Data Source={dbName};Mode=Memory;Cache=Shared";

        // Keeper: stays open for the test duration to prevent database destruction
        var keeper = new SqliteConnection(connStr);
        keeper.Open();

        // Apply analytics migrations via a shared-cache factory adapter
        var analyticsFactory = new SharedAnalyticsCacheFactory(connStr);
        var adapter = new AnalyticsConnectionFactoryAdapter(analyticsFactory);

        using var migConn = new SqliteConnection(connStr);
        migConn.Open();

        var runner = new MigrationRunner(adapter, NullLogger<MigrationRunner>.Instance, "AnalyticsMigrations");
        await runner.ApplyMigrationsAsync(migConn);

        return (keeper, analyticsFactory);
    }

    /// <summary>
    /// Adapts <see cref="IAnalyticsDbConnectionFactory"/> to <see cref="IDbConnectionFactory"/>
    /// for use with <see cref="MigrationRunner"/>.
    /// </summary>
    private sealed class AnalyticsConnectionFactoryAdapter(IAnalyticsDbConnectionFactory inner) : IDbConnectionFactory
    {
        public System.Data.IDbConnection CreateConnection() => inner.CreateConnection();
    }

    /// <summary>
    /// Factory that creates new connections to the same named shared-cache in-memory analytics database.
    /// </summary>
    internal sealed class SharedAnalyticsCacheFactory(string connectionString) : IAnalyticsDbConnectionFactory
    {
        public System.Data.IDbConnection CreateConnection()
        {
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            return conn;
        }
    }
}
