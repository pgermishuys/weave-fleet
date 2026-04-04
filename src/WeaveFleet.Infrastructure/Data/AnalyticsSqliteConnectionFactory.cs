using System.Data;
using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Infrastructure.Data;

/// <summary>
/// Implements <see cref="IAnalyticsDbConnectionFactory"/> using Microsoft.Data.Sqlite.
/// Opens connections to the separate analytics database with WAL mode and busy_timeout.
/// Foreign keys are NOT enabled — the analytics DB has no FK references to the main DB.
/// </summary>
public sealed class AnalyticsSqliteConnectionFactory : IAnalyticsDbConnectionFactory
{
    private readonly string _connectionString;

    public AnalyticsSqliteConnectionFactory(string analyticsDbPath)
    {
        _connectionString = $"Data Source={analyticsDbPath}";

        // Ensure parent directory exists on construction
        var dir = Path.GetDirectoryName(analyticsDbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public IDbConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Apply pragmas for performance — no foreign_keys=ON (analytics DB is standalone)
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            """;
        cmd.ExecuteNonQuery();

        return connection;
    }
}
