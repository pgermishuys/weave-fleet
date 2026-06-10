using System.Data;
using System.Data.Common;
using DbUp.Engine;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Data;

internal static partial class LegacyMigrationJournalBridge
{
    private const string LegacyJournalTable = "_migrations";

    public static async Task ApplyAsync(
        IDbConnection connection,
        IReadOnlyList<SqlScript> scripts,
        string journalTable,
        string folderSegment,
        ILogger logger)
    {
        if (!await TableExistsAsync(connection, LegacyJournalTable))
            return;

        var appliedLegacyNames = (await connection.QueryAsync(
            $"SELECT name FROM {LegacyJournalTable} ORDER BY id",
            r => r.GetString(0))).ToList();

        if (folderSegment == "Migrations")
            await RepairKnownMainDatabaseStatesAsync(connection, appliedLegacyNames, logger);

        if (await TableExistsAsync(connection, journalTable)
            && (await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {journalTable}", static cmd => { })) > 0)
        {
            return;
        }

        if (appliedLegacyNames.Count == 0)
            return;

        await EnsureDbUpJournalAsync(connection, journalTable);

        var scriptNamesByLegacyName = scripts
            .ToDictionary(script => MigrationRunner.ExtractMigrationName(script.Name), script => script.Name, StringComparer.Ordinal);

        foreach (var legacyName in appliedLegacyNames)
        {
            if (!scriptNamesByLegacyName.TryGetValue(legacyName, out var dbUpScriptName))
            {
                throw new InvalidOperationException(
                    $"Legacy migration '{legacyName}' from {LegacyJournalTable} cannot be mapped to an embedded DbUp script for segment '{folderSegment}'.");
            }

            await connection.ExecuteNonQueryAsync(
                $"INSERT INTO {journalTable} (ScriptName, Applied) VALUES (@ScriptName, datetime('now'))",
                cmd => { cmd.AddParameter("ScriptName", dbUpScriptName); });
        }

        LogSeededDbUpJournal(logger, journalTable, appliedLegacyNames.Count, folderSegment);
    }

    private static async Task RepairKnownMainDatabaseStatesAsync(
        IDbConnection connection,
        List<string> appliedLegacyNames,
        ILogger logger)
    {
        var projectsTableExists = await TableExistsAsync(connection, "projects");
        var workspacesTableExists = await TableExistsAsync(connection, "workspaces");

        if (appliedLegacyNames.Count > 0 && !projectsTableExists)
        {
            LogCorruptMigrationHistory(logger);
            await connection.ExecuteNonQueryAsync($"DELETE FROM {LegacyJournalTable}");
            appliedLegacyNames.Clear();
        }

        if (appliedLegacyNames.Count == 0 && workspacesTableExists)
        {
            LogLegacyDatabaseDetected(logger);
            await DropLegacyTablesAsync(connection);
        }
    }

    private static async Task EnsureDbUpJournalAsync(IDbConnection connection, string journalTable)
    {
        await connection.ExecuteNonQueryAsync($$"""
            CREATE TABLE IF NOT EXISTS {{journalTable}} (
                SchemaVersionId INTEGER PRIMARY KEY AUTOINCREMENT,
                ScriptName TEXT NOT NULL,
                Applied TEXT NOT NULL
            )
            """);
    }

    private static async Task<bool> TableExistsAsync(IDbConnection connection, string tableName)
    {
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@TableName",
            cmd => { cmd.AddParameter("TableName", tableName); });
        return count > 0;
    }

    private static async Task DropLegacyTablesAsync(IDbConnection connection)
    {
        string[] legacyTables =
        [
            "session_callbacks",
            "sessions",
            "instances",
            "workspaces",
            "workspace_roots",
        ];

        foreach (var table in legacyTables)
            await connection.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {table}");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded DbUp journal {JournalTable} from {Count} legacy migration entries for segment {FolderSegment}.")]
    private static partial void LogSeededDbUpJournal(ILogger logger, string journalTable, int count, string folderSegment);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Corrupt migration history detected; clearing legacy journal so DbUp can rebuild the schema.")]
    private static partial void LogCorruptMigrationHistory(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Legacy database detected; dropping incompatible tables so DbUp migrations can recreate them.")]
    private static partial void LogLegacyDatabaseDetected(ILogger logger);
}
