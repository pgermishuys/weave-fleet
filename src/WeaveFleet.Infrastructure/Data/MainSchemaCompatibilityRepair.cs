using System.Data;
using DbUp.Engine;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Data;

internal static partial class MainSchemaCompatibilityRepair
{
    private const string RuntimeModeMigrationName = "024_add_session_runtime_mode.sql";

    public static async Task ApplyAsync(
        IDbConnection connection,
        IReadOnlyList<SqlScript> scripts,
        string journalTable,
        ILogger logger)
    {
        var runtimeModeScript = scripts.FirstOrDefault(script =>
            string.Equals(
                MigrationRunner.ExtractMigrationName(script.Name),
                RuntimeModeMigrationName,
                StringComparison.Ordinal));

        if (runtimeModeScript is null || !await TableExistsAsync(connection, "sessions"))
            return;

        var hasRuntimeModeColumn = await ColumnExistsAsync(connection, "sessions", "runtime_mode");
        var hasJournal = await TableExistsAsync(connection, journalTable);
        var journalContainsRuntimeModeMigration = hasJournal
            && await JournalContainsScriptAsync(connection, journalTable, runtimeModeScript.Name);

        if (!hasRuntimeModeColumn && journalContainsRuntimeModeMigration)
        {
            await AddRuntimeModeColumnAsync(connection);
            LogRepairedMissingRuntimeModeColumn(logger);
            return;
        }

        if (hasRuntimeModeColumn && hasJournal && !journalContainsRuntimeModeMigration)
        {
            await connection.ExecuteNonQueryAsync(
                $"INSERT INTO {journalTable} (ScriptName, Applied) VALUES (@ScriptName, datetime('now'))",
                cmd => { cmd.AddParameter("ScriptName", runtimeModeScript.Name); });
            LogStampedExistingRuntimeModeColumn(logger);
        }
    }

    private static async Task AddRuntimeModeColumnAsync(IDbConnection connection)
    {
        await connection.ExecuteNonQueryAsync("ALTER TABLE sessions ADD COLUMN runtime_mode TEXT NOT NULL DEFAULT 'manual'");
        await connection.ExecuteNonQueryAsync(
            "UPDATE sessions SET runtime_mode = 'manual' WHERE runtime_mode IS NULL OR runtime_mode = ''");
    }

    private static async Task<bool> TableExistsAsync(IDbConnection connection, string tableName)
    {
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@TableName",
            cmd => { cmd.AddParameter("TableName", tableName); });
        return count > 0;
    }

    private static async Task<bool> ColumnExistsAsync(IDbConnection connection, string tableName, string columnName)
    {
        var count = await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = @ColumnName",
            cmd => { cmd.AddParameter("ColumnName", columnName); });
        return count > 0;
    }

    private static async Task<bool> JournalContainsScriptAsync(
        IDbConnection connection,
        string journalTable,
        string scriptName)
    {
        var count = await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {journalTable} WHERE ScriptName = @ScriptName",
            cmd => { cmd.AddParameter("ScriptName", scriptName); });
        return count > 0;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repaired sessions.runtime_mode because migration 024 was journaled but the column was missing.")]
    private static partial void LogRepairedMissingRuntimeModeColumn(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked migration 024 applied because sessions.runtime_mode already exists.")]
    private static partial void LogStampedExistingRuntimeModeColumn(ILogger logger);
}
