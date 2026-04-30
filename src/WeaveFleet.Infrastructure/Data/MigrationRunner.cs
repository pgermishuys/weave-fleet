using System.Data;
using System.Reflection;
using DbUp;
using DbUp.Engine;
using DbUp.Sqlite.Helpers;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Infrastructure.Data;

/// <summary>
/// Applies embedded SQLite migrations with DbUp.
/// A bounded startup bridge seeds DbUp's journal from legacy <c>_migrations</c> rows.
/// </summary>
public sealed partial class MigrationRunner
{
    internal const string MainJournalTable = "schema_versions";

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<MigrationRunner> _logger;
    private readonly string _folderSegment;
    private readonly string _journalTable;

    public MigrationRunner(
        IDbConnectionFactory connectionFactory,
        ILogger<MigrationRunner> logger,
        string folderSegment = "Migrations")
        : this(connectionFactory, logger, folderSegment, MainJournalTable)
    {
    }

    internal MigrationRunner(
        IDbConnectionFactory connectionFactory,
        ILogger<MigrationRunner> logger,
        string folderSegment,
        string journalTable)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _folderSegment = folderSegment;
        _journalTable = journalTable;
    }

    public async Task ApplyMigrationsAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        await ApplyMigrationsAsync(connection);
    }

    public async Task ApplyMigrationsAsync(IDbConnection connection)
    {
        var scripts = LoadScripts(_folderSegment);
        if (scripts.Count == 0)
        {
            LogNoMigrationsFound(_folderSegment);
            return;
        }

        await LegacyMigrationJournalBridge.ApplyAsync(connection, scripts, _journalTable, _folderSegment, _logger);

        var result = DeployChanges.To
            .SqliteDatabase(new SharedConnection(connection))
            .WithScripts(scripts)
            .WithScriptNameComparer(StringComparer.Ordinal)
            .WithTransactionPerScript()
            .JournalToSqliteTable(_journalTable)
            .LogTo(_logger)
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
        {
            LogMigrationFailed(result.Error, _folderSegment);
            throw result.Error;
        }
    }

    internal static IReadOnlyList<SqlScript> LoadScripts(string folderSegment)
    {
        var assembly = typeof(MigrationRunner).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => IsMigrationResource(name, folderSegment))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new SqlScript(name, ReadResource(assembly, name)))
            .ToList();
    }

    internal static bool IsMigrationResource(string resourceName, string folderSegment)
        => resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            && resourceName.Split('.').Contains(folderSegment, StringComparer.Ordinal);

    internal static string ExtractMigrationName(string resourceName)
    {
        var parts = resourceName.Split('.');
        return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : resourceName;
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No SQL migration resources found for segment {FolderSegment}.")]
    private partial void LogNoMigrationsFound(string folderSegment);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to apply DbUp migrations for segment {FolderSegment}.")]
    private partial void LogMigrationFailed(Exception ex, string folderSegment);
}
