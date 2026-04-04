using System.Data;
using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Infrastructure.Data;

/// <summary>
/// Applies numbered SQL migration files from embedded resources to a SQLite database.
/// Tracks applied migrations in a <c>_migrations</c> table.
/// Only processes resources whose dotted resource name contains <paramref name="folderSegment"/>
/// as an exact dotted-segment match (not a substring). Default segment is <c>"Migrations"</c>.
/// </summary>
public sealed partial class MigrationRunner
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<MigrationRunner> _logger;
    private readonly string _folderSegment;

    public MigrationRunner(
        IDbConnectionFactory connectionFactory,
        ILogger<MigrationRunner> logger,
        string folderSegment = "Migrations")
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _folderSegment = folderSegment;
    }

    public async Task ApplyMigrationsAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        await ApplyMigrationsAsync(connection);
    }

    public async Task ApplyMigrationsAsync(IDbConnection connection)
    {
        // Ensure the migrations tracking table exists
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS _migrations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                applied_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);

        // Load all embedded SQL migration resources filtered by exact dotted-segment match
        var assembly = typeof(MigrationRunner).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                        && n.Split('.').Contains(_folderSegment, StringComparer.Ordinal))
            .OrderBy(n => n)
            .ToList();

        if (resourceNames.Count == 0)
        {
            LogNoMigrationsFound();
            return;
        }

        // Get already-applied migrations
        var applied = (await connection.QueryAsync<string>("SELECT name FROM _migrations")).ToHashSet();

        foreach (var resourceName in resourceNames)
        {
            var migrationName = ExtractMigrationName(resourceName);

            if (applied.Contains(migrationName))
            {
                LogSkippingMigration(migrationName);
                continue;
            }

            var sql = ReadResource(assembly, resourceName);

            LogApplyingMigration(migrationName);

            // Execute in a transaction
            using var tx = connection.BeginTransaction();
            try
            {
                await connection.ExecuteAsync(sql, transaction: tx);
                await connection.ExecuteAsync(
                    "INSERT INTO _migrations (name) VALUES (@Name)",
                    new { Name = migrationName },
                    transaction: tx);
                tx.Commit();
                LogMigrationApplied(migrationName);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                LogMigrationFailed(ex, migrationName);
                throw;
            }
        }
    }

    private static string ExtractMigrationName(string resourceName)
    {
        // e.g. "WeaveFleet.Infrastructure.Migrations.001_initial_schema.sql"
        // -> "001_initial_schema.sql"
        var parts = resourceName.Split('.');
        if (parts.Length >= 2)
            return $"{parts[^2]}.{parts[^1]}";
        return resourceName;
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No SQL migration resources found.")]
    private partial void LogNoMigrationsFound();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping already-applied migration: {MigrationName}")]
    private partial void LogSkippingMigration(string migrationName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying migration: {MigrationName}")]
    private partial void LogApplyingMigration(string migrationName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migration applied successfully: {MigrationName}")]
    private partial void LogMigrationApplied(string migrationName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to apply migration: {MigrationName}")]
    private partial void LogMigrationFailed(Exception ex, string migrationName);
}
