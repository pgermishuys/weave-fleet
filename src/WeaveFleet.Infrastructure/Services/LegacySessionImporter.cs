using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Data;

namespace WeaveFleet.Infrastructure.Services;

public sealed class LegacySessionImporter : ILegacySessionImporter
{
    private const string ImportedProjectName = "Imported Legacy Sessions";
    private const string ImportedProjectType = "user";
    private const string ImportedProjectId = "legacy-imported-sessions-project";
    private const string SentinelWorkspaceId = "legacy-import-workspace";
    private const string SentinelInstanceId = "legacy-import-instance";
    private const string CompletedStatus = "completed";
    private const string SkippedStatus = "skipped";
    private const string NotFoundStatus = "not_found";

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IUserContext _userContext;
    private readonly ILegacyDatabaseConnectionFactory _legacyConnectionFactory;

    public LegacySessionImporter(
        IDbConnectionFactory connectionFactory,
        IUserContext userContext)
        : this(connectionFactory, userContext, new FileLegacyDatabaseConnectionFactory())
    {
    }

    internal LegacySessionImporter(
        IDbConnectionFactory connectionFactory,
        IUserContext userContext,
        ILegacyDatabaseConnectionFactory legacyConnectionFactory)
    {
        _connectionFactory = connectionFactory;
        _userContext = userContext;
        _legacyConnectionFactory = legacyConnectionFactory;
    }

    public Task<LegacySessionImportResult> ImportAsync()
        => ImportAsync(CancellationToken.None);

    public Task<LegacySessionImportResult> ImportAsync(CancellationToken cancellationToken)
        => ImportAsync(GetDefaultLegacyDatabasePath(), cancellationToken);

    public Task<LegacySessionImportResult> ImportAsync(string sourcePath)
        => ImportAsync(sourcePath, CancellationToken.None);

    public async Task<LegacySessionImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var normalizedSourcePath = NormalizeSourcePath(sourcePath);

        using var connection = _connectionFactory.CreateConnection();

        var existingImport = await GetCompletedImportAsync(connection, normalizedSourcePath, null, cancellationToken)
            .ConfigureAwait(false);
        if (existingImport is not null)
            return new LegacySessionImportResult(false, true, normalizedSourcePath, existingImport.SessionCount, SkippedStatus);

        if (!_legacyConnectionFactory.Exists(normalizedSourcePath))
            return new LegacySessionImportResult(false, true, normalizedSourcePath, 0, NotFoundStatus);

        await using var legacyConnection = await _legacyConnectionFactory.OpenReadOnlyAsync(normalizedSourcePath, cancellationToken)
            .ConfigureAwait(false);

        var legacyData = await ReadLegacyDataAsync(legacyConnection, cancellationToken).ConfigureAwait(false);
        var importedAt = DateTimeOffset.UtcNow.ToString("O");
        var importId = CreateImportId(normalizedSourcePath);

        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            existingImport = await GetCompletedImportAsync(connection, normalizedSourcePath, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (existingImport is not null)
            {
                transaction.Commit();
                return new LegacySessionImportResult(false, true, normalizedSourcePath, existingImport.SessionCount, SkippedStatus);
            }

            EnsureNoDuplicateLegacySessionIds(legacyData.Sessions);
            await EnsureNoDestinationSessionIdCollisionsAsync(connection, transaction, legacyData.Sessions, cancellationToken)
                .ConfigureAwait(false);

            var projectId = await EnsureImportedProjectAsync(connection, transaction, importedAt, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSentinelWorkspaceAsync(connection, transaction, normalizedSourcePath, importedAt, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSentinelInstanceAsync(connection, transaction, normalizedSourcePath, importedAt, cancellationToken)
                .ConfigureAwait(false);

            foreach (var session in legacyData.Sessions)
            {
                var mapped = MapSession(session, legacyData, projectId, importedAt);
                await InsertSessionAsync(connection, transaction, mapped, cancellationToken).ConfigureAwait(false);
            }

            await InsertImportMarkerAsync(
                    connection,
                    transaction,
                    importId,
                    normalizedSourcePath,
                    importedAt,
                    legacyData.Sessions.Count,
                    cancellationToken)
                .ConfigureAwait(false);

            transaction.Commit();
            return new LegacySessionImportResult(true, false, normalizedSourcePath, legacyData.Sessions.Count, CompletedStatus);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    internal static StatusNormalization NormalizeStatus(string? legacyStatus)
    {
        var normalizedLegacyStatus = string.IsNullOrWhiteSpace(legacyStatus)
            ? string.Empty
            : legacyStatus.Trim().ToLowerInvariant();

        return normalizedLegacyStatus switch
        {
            "active" or "running" or "idle" => new StatusNormalization("stopped", "completed", null, true),
            "waiting_input" => new StatusNormalization("stopped", "completed", null, false),
            "stopped" or "completed" or "error" or "disconnected" => new StatusNormalization(normalizedLegacyStatus, normalizedLegacyStatus, null, false),
            _ => new StatusNormalization("stopped", "completed", null, false),
        };
    }

    private static async Task<LegacyData> ReadLegacyDataAsync(DbConnection legacyConnection, CancellationToken cancellationToken)
    {
        var sessions = await ReadTableAsync(legacyConnection, "sessions", cancellationToken).ConfigureAwait(false);
        var workspaces = await ReadTableAsync(legacyConnection, "workspaces", cancellationToken).ConfigureAwait(false);
        var instances = await ReadTableAsync(legacyConnection, "instances", cancellationToken).ConfigureAwait(false);

        return new LegacyData(sessions, workspaces, instances);
    }

    private static async Task<List<LegacyRow>> ReadTableAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {tableName}";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<LegacyRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

            rows.Add(new LegacyRow(values));
        }

        return rows;
    }

    private static async Task<ImportMarker?> GetCompletedImportAsync(
        IDbConnection connection,
        string sourcePath,
        IDbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT session_count
            FROM legacy_imports
            WHERE source_path = @SourcePath AND status = @Status
            LIMIT 1
            """);
        command.AddParameter("SourcePath", sourcePath);
        command.AddParameter("Status", CompletedStatus);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : new ImportMarker(Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<string> EnsureImportedProjectAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await using (var lookup = CreateCommand(
                         connection,
                         transaction,
                         """
                         SELECT id
                         FROM projects
                         WHERE name = @Name AND type = @Type AND user_id = @UserId
                         LIMIT 1
                         """))
        {
            lookup.AddParameter("Name", ImportedProjectName);
            lookup.AddParameter("Type", ImportedProjectType);
            lookup.AddParameter("UserId", _userContext.UserId);
            var existingId = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (!string.IsNullOrWhiteSpace(existingId))
                return existingId;
        }

        await EnsureIdAvailableAsync(
                connection,
                transaction,
                "projects",
                ImportedProjectId,
                cancellationToken)
            .ConfigureAwait(false);

        var position = await GetNextProjectPositionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        await using var insert = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO projects (id, name, description, type, position, created_at, updated_at, user_id)
            VALUES (@Id, @Name, @Description, @Type, @Position, @CreatedAt, @UpdatedAt, @UserId)
            """);
        insert.AddParameter("Id", ImportedProjectId);
        insert.AddParameter("Name", ImportedProjectName);
        insert.AddParameter("Description", "Sessions imported from the legacy Fleet database.");
        insert.AddParameter("Type", ImportedProjectType);
        insert.AddParameter("Position", position);
        insert.AddParameter("CreatedAt", timestamp);
        insert.AddParameter("UpdatedAt", timestamp);
        insert.AddParameter("UserId", _userContext.UserId);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return ImportedProjectId;
    }

    private async Task EnsureSentinelWorkspaceAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string sourcePath,
        string timestamp,
        CancellationToken cancellationToken)
    {
        if (await ExistingRowBelongsToUserAsync(connection, transaction, "workspaces", SentinelWorkspaceId, cancellationToken)
                .ConfigureAwait(false))
            return;

        await EnsureIdAvailableAsync(connection, transaction, "workspaces", SentinelWorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        await using var insert = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO workspaces (
                id, directory, source_directory, isolation_strategy, branch, created_at, cleaned_up_at, display_name,
                source_provider_id, source_type, source_resource_id, source_resource_url, source_title, source_summary,
                source_resolved_at, user_id)
            VALUES (
                @Id, @Directory, @SourceDirectory, @IsolationStrategy, @Branch, @CreatedAt, @CleanedUpAt, @DisplayName,
                @SourceProviderId, @SourceType, @SourceResourceId, @SourceResourceUrl, @SourceTitle, @SourceSummary,
                @SourceResolvedAt, @UserId)
            """);
        insert.AddParameter("Id", SentinelWorkspaceId);
        insert.AddParameter("Directory", GetSentinelDirectory(sourcePath));
        insert.AddParameter("SourceDirectory", null);
        insert.AddParameter("IsolationStrategy", "legacy-import");
        insert.AddParameter("Branch", null);
        insert.AddParameter("CreatedAt", timestamp);
        insert.AddParameter("CleanedUpAt", timestamp);
        insert.AddParameter("DisplayName", ImportedProjectName);
        insert.AddParameter("SourceProviderId", "legacy-fleet");
        insert.AddParameter("SourceType", "legacy-import");
        insert.AddParameter("SourceResourceId", sourcePath);
        insert.AddParameter("SourceResourceUrl", null);
        insert.AddParameter("SourceTitle", ImportedProjectName);
        insert.AddParameter("SourceSummary", "Sentinel workspace for imported legacy sessions.");
        insert.AddParameter("SourceResolvedAt", timestamp);
        insert.AddParameter("UserId", _userContext.UserId);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSentinelInstanceAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string sourcePath,
        string timestamp,
        CancellationToken cancellationToken)
    {
        if (await ExistingRowBelongsToUserAsync(connection, transaction, "instances", SentinelInstanceId, cancellationToken)
                .ConfigureAwait(false))
            return;

        await EnsureIdAvailableAsync(connection, transaction, "instances", SentinelInstanceId, cancellationToken)
            .ConfigureAwait(false);

        await using var insert = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, stopped_at, user_id)
            VALUES (@Id, @Port, @Pid, @Directory, @Url, @Status, @CreatedAt, @StoppedAt, @UserId)
            """);
        insert.AddParameter("Id", SentinelInstanceId);
        insert.AddParameter("Port", 0);
        insert.AddParameter("Pid", null);
        insert.AddParameter("Directory", GetSentinelDirectory(sourcePath));
        insert.AddParameter("Url", "");
        insert.AddParameter("Status", "stopped");
        insert.AddParameter("CreatedAt", timestamp);
        insert.AddParameter("StoppedAt", timestamp);
        insert.AddParameter("UserId", _userContext.UserId);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GetNextProjectPositionAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT COALESCE(MAX(position), -1) + 1 FROM projects");
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<bool> ExistingRowBelongsToUserAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string tableName,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"SELECT user_id FROM {tableName} WHERE id = @Id LIMIT 1");
        command.AddParameter("Id", id);
        var userId = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.Equals(userId, _userContext.UserId, StringComparison.Ordinal);
    }

    private static async Task EnsureIdAvailableAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string tableName,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"SELECT COUNT(*) FROM {tableName} WHERE id = @Id");
        command.AddParameter("Id", id);
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count > 0)
            throw new InvalidOperationException($"Cannot import legacy sessions because id '{id}' already exists in '{tableName}'.");
    }

    private static async Task EnsureNoDestinationSessionIdCollisionsAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        IReadOnlyList<LegacyRow> legacySessions,
        CancellationToken cancellationToken)
    {
        foreach (var session in legacySessions)
        {
            var id = GetRequiredString(session, "id");
            await using var command = CreateCommand(
                connection,
                transaction,
                "SELECT COUNT(*) FROM sessions WHERE id = @Id");
            command.AddParameter("Id", id);
            var count = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0)
                throw new InvalidOperationException($"Cannot import legacy session '{id}' because a session with that id already exists.");
        }
    }

    private static void EnsureNoDuplicateLegacySessionIds(IReadOnlyList<LegacyRow> legacySessions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in legacySessions)
        {
            var id = GetRequiredString(session, "id");
            if (!seen.Add(id))
                throw new InvalidOperationException($"Cannot import legacy sessions because source contains duplicate session id '{id}'.");
        }
    }

    private SessionRow MapSession(
        LegacyRow legacySession,
        LegacyData legacyData,
        string projectId,
        string importedAt)
    {
        var id = GetRequiredString(legacySession, "id");
        var workspaceDirectory = GetReferencedDirectory(legacyData.Workspaces, GetString(legacySession, "workspace_id"));
        var instanceDirectory = GetReferencedDirectory(legacyData.Instances, GetString(legacySession, "instance_id"));
        var directory = FirstNonWhiteSpace(
            GetString(legacySession, "directory"),
            workspaceDirectory,
            instanceDirectory,
            GetSentinelDirectory(string.Empty));
        var createdAt = FirstNonWhiteSpace(GetString(legacySession, "created_at"), importedAt);
        var stoppedAt = GetString(legacySession, "stopped_at");
        var normalizedStatus = NormalizeStatus(GetString(legacySession, "status"));
        if (normalizedStatus.UseImportTimestampForStoppedAt && string.IsNullOrWhiteSpace(stoppedAt))
            stoppedAt = importedAt;

        return new SessionRow(
            id,
            SentinelWorkspaceId,
            SentinelInstanceId,
            projectId,
            FirstNonWhiteSpace(GetString(legacySession, "opencode_session_id"), id),
            FirstNonWhiteSpace(GetString(legacySession, "title"), "Untitled"),
            normalizedStatus.Status,
            directory,
            createdAt,
            stoppedAt,
            GetString(legacySession, "parent_session_id"),
            normalizedStatus.ActivityStatus,
            normalizedStatus.LifecycleStatus,
            "active",
            null,
            GetBoolean(legacySession, "is_hidden"),
            GetInt32(legacySession, "total_tokens"),
            GetDouble(legacySession, "total_cost"),
            FirstNonWhiteSpace(GetString(legacySession, "harness_type"), "opencode"),
            GetString(legacySession, "harness_resume_token"),
            _userContext.UserId);
    }

    private static async Task InsertSessionAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        SessionRow session,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO sessions (id, workspace_id, instance_id, project_id, opencode_session_id, title,
                status, directory, created_at, stopped_at, parent_session_id, activity_status,
                lifecycle_status, retention_status, archived_at, is_hidden, total_tokens, total_cost,
                harness_type, harness_resume_token, user_id)
            VALUES (@Id, @WorkspaceId, @InstanceId, @ProjectId, @OpencodeSessionId, @Title,
                @Status, @Directory, @CreatedAt, @StoppedAt, @ParentSessionId, @ActivityStatus,
                @LifecycleStatus, @RetentionStatus, @ArchivedAt, @IsHidden, @TotalTokens, @TotalCost,
                @HarnessType, @HarnessResumeToken, @UserId)
            """);
        command.AddParameter("Id", session.Id);
        command.AddParameter("WorkspaceId", session.WorkspaceId);
        command.AddParameter("InstanceId", session.InstanceId);
        command.AddParameter("ProjectId", session.ProjectId);
        command.AddParameter("OpencodeSessionId", session.OpencodeSessionId);
        command.AddParameter("Title", session.Title);
        command.AddParameter("Status", session.Status);
        command.AddParameter("Directory", session.Directory);
        command.AddParameter("CreatedAt", session.CreatedAt);
        command.AddParameter("StoppedAt", session.StoppedAt);
        command.AddParameter("ParentSessionId", session.ParentSessionId);
        command.AddParameter("ActivityStatus", session.ActivityStatus);
        command.AddParameter("LifecycleStatus", session.LifecycleStatus);
        command.AddParameter("RetentionStatus", session.RetentionStatus);
        command.AddParameter("ArchivedAt", session.ArchivedAt);
        command.AddParameter("IsHidden", session.IsHidden);
        command.AddParameter("TotalTokens", session.TotalTokens);
        command.AddParameter("TotalCost", session.TotalCost);
        command.AddParameter("HarnessType", session.HarnessType);
        command.AddParameter("HarnessResumeToken", session.HarnessResumeToken);
        command.AddParameter("UserId", session.UserId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertImportMarkerAsync(
        IDbConnection connection,
        IDbTransaction transaction,
        string id,
        string sourcePath,
        string importedAt,
        int sessionCount,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO legacy_imports (id, source_path, imported_at, session_count, status)
            VALUES (@Id, @SourcePath, @ImportedAt, @SessionCount, @Status)
            """);
        command.AddParameter("Id", id);
        command.AddParameter("SourcePath", sourcePath);
        command.AddParameter("ImportedAt", importedAt);
        command.AddParameter("SessionCount", sessionCount);
        command.AddParameter("Status", CompletedStatus);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbCommand CreateCommand(IDbConnection connection, IDbTransaction? transaction, string commandText)
    {
        var command = ((DbConnection)connection).CreateCommand();
        command.CommandText = commandText;
        if (transaction is not null)
            command.Transaction = (DbTransaction)transaction;
        return command;
    }

    private static string? GetReferencedDirectory(IReadOnlyList<LegacyRow> rows, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var row = rows.FirstOrDefault(candidate => string.Equals(GetString(candidate, "id"), id, StringComparison.Ordinal));
        return row is null ? null : GetString(row, "directory");
    }

    private static string GetRequiredString(LegacyRow row, string columnName)
    {
        var value = GetString(row, columnName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Legacy row is missing required column '{columnName}'.");
        return value;
    }

    private static string? GetString(LegacyRow row, string columnName)
    {
        if (!row.Values.TryGetValue(columnName, out var value) || value is null or DBNull)
            return null;

        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int GetInt32(LegacyRow row, string columnName)
    {
        if (!row.Values.TryGetValue(columnName, out var value) || value is null or DBNull)
            return 0;

        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double GetDouble(LegacyRow row, string columnName)
    {
        if (!row.Values.TryGetValue(columnName, out var value) || value is null or DBNull)
            return 0;

        return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool GetBoolean(LegacyRow row, string columnName)
    {
        if (!row.Values.TryGetValue(columnName, out var value) || value is null or DBNull)
            return false;

        return value switch
        {
            bool boolean => boolean,
            long number => number != 0,
            int number => number != 0,
            string text => text == "1" || bool.TryParse(text, out var parsed) && parsed,
            _ => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) != 0,
        };
    }

    private static string FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static string GetSentinelDirectory(string sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            var directory = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrWhiteSpace(directory))
                return directory;
        }

        return Path.Combine(GetUserProfileDirectory(), ".weave");
    }

    private static string GetDefaultLegacyDatabasePath()
        => Path.Combine(GetUserProfileDirectory(), ".weave", "fleet.db");

    private static string GetUserProfileDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? Environment.CurrentDirectory : home;
    }

    private static string NormalizeSourcePath(string sourcePath)
    {
        var expanded = sourcePath.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(GetUserProfileDirectory(), sourcePath[2..])
            : sourcePath;

        return Path.GetFullPath(expanded);
    }

    private static string CreateImportId(string sourcePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath));
        return "legacy-import-" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record ImportMarker(int SessionCount);

    internal sealed record StatusNormalization(
        string Status,
        string LifecycleStatus,
        string? ActivityStatus,
        bool UseImportTimestampForStoppedAt);

    private sealed record LegacyData(
        IReadOnlyList<LegacyRow> Sessions,
        IReadOnlyList<LegacyRow> Workspaces,
        IReadOnlyList<LegacyRow> Instances);

    private sealed record LegacyRow(IReadOnlyDictionary<string, object?> Values);

    private sealed record SessionRow(
        string Id,
        string WorkspaceId,
        string InstanceId,
        string ProjectId,
        string OpencodeSessionId,
        string Title,
        string Status,
        string Directory,
        string CreatedAt,
        string? StoppedAt,
        string? ParentSessionId,
        string? ActivityStatus,
        string? LifecycleStatus,
        string RetentionStatus,
        string? ArchivedAt,
        bool IsHidden,
        int TotalTokens,
        double TotalCost,
        string HarnessType,
        string? HarnessResumeToken,
        string UserId);
}

internal interface ILegacyDatabaseConnectionFactory
{
    bool Exists(string sourcePath);
    Task<DbConnection> OpenReadOnlyAsync(string sourcePath, CancellationToken cancellationToken);
}

internal sealed class FileLegacyDatabaseConnectionFactory : ILegacyDatabaseConnectionFactory
{
    public bool Exists(string sourcePath) => File.Exists(sourcePath);

    public async Task<DbConnection> OpenReadOnlyAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only = ON";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }
}
