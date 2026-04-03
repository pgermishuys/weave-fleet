using System.Data;
using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Infrastructure.Data;

/// <summary>
/// Implements <see cref="IDbConnectionFactory"/> using Microsoft.Data.Sqlite.
/// Opens and configures connections with WAL mode, busy_timeout, and foreign key support.
/// </summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(FleetOptions options)
    {
        _connectionString = $"Data Source={options.DatabasePath}";

        // Ensure parent directory exists on construction
        var dir = Path.GetDirectoryName(options.DatabasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public IDbConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Apply pragmas for performance and correctness
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            """;
        cmd.ExecuteNonQuery();

        return connection;
    }
}
