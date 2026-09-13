using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class AppRunRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IAppRunRepository
{
    public async Task<AppRun?> GetByIdAsync(string sessionId, string appId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM app_runs WHERE id = @Id AND session_id = @SessionId AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", appId);
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadRun);
    }

    public async Task<IReadOnlyList<AppRun>> ListBySessionIdAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT * FROM app_runs
            WHERE session_id = @SessionId AND user_id = @UserId
            ORDER BY created_at ASC, id ASC
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadRun);
    }

    public async Task<bool> UpsertAsync(AppRun run)
    {
        using var conn = connectionFactory.CreateConnection();

        // The session has to be the current user's, and an existing row keeps its session and owner:
        // the conflict update only applies when both still match.
        var rows = await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO app_runs (id, session_id, user_id, command, directory, port, status, exit_code, url,
                pid, pid_started_at, created_at, updated_at)
            SELECT @Id, @SessionId, @UserId, @Command, @Directory, @Port, @Status, @ExitCode, @Url,
                @Pid, @PidStartedAt, @CreatedAt, @UpdatedAt
            FROM sessions session_row
            WHERE session_row.id = @SessionId AND session_row.user_id = @UserId
            ON CONFLICT(id) DO UPDATE SET
                command = excluded.command,
                directory = excluded.directory,
                port = excluded.port,
                status = excluded.status,
                exit_code = excluded.exit_code,
                url = excluded.url,
                pid = excluded.pid,
                pid_started_at = excluded.pid_started_at,
                updated_at = excluded.updated_at
            WHERE app_runs.session_id = excluded.session_id AND app_runs.user_id = excluded.user_id
            """,
            cmd =>
            {
                cmd.AddParameter("Id", run.Id);
                cmd.AddParameter("SessionId", run.SessionId);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("Command", run.Command);
                cmd.AddParameter("Directory", run.Directory);
                cmd.AddParameter("Port", run.Port);
                cmd.AddParameter("Status", run.Status);
                cmd.AddParameter("ExitCode", run.ExitCode);
                cmd.AddParameter("Url", run.Url);
                cmd.AddParameter("Pid", run.Pid);
                cmd.AddParameter("PidStartedAt", run.PidStartedAt);
                cmd.AddParameter("CreatedAt", run.CreatedAt);
                cmd.AddParameter("UpdatedAt", run.UpdatedAt);
            });
        return rows > 0;
    }

    public async Task<IReadOnlyList<AppRun>> ListUnfinishedForAllUsersAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT * FROM app_runs WHERE status NOT IN ('exited', 'stopped') ORDER BY created_at ASC, id ASC",
            _ => { },
            ReadRun);
    }

    public async Task MarkStoppedForAllUsersAsync(IReadOnlyCollection<string> appIds, string updatedAt)
    {
        if (appIds.Count == 0)
            return;

        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        foreach (var appId in appIds)
        {
            await conn.ExecuteNonQueryAsync(
                """
                UPDATE app_runs
                SET status = 'stopped', exit_code = NULL, pid = NULL, pid_started_at = NULL, updated_at = @UpdatedAt
                WHERE id = @Id
                """,
                cmd =>
                {
                    cmd.AddParameter("Id", appId);
                    cmd.AddParameter("UpdatedAt", updatedAt);
                },
                tx);
        }
        tx.Commit();
    }

    // A session's project: the folder its worktree came from, or the session's own folder.
    private const string ProjectDirectorySql = "COALESCE(workspace_row.source_directory, session_row.directory)";

    public async Task RememberPreviewCommandAsync(string sessionId, string command, string updatedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            $"""
            INSERT INTO preview_commands (user_id, project_directory, command, updated_at)
            SELECT session_row.user_id, {ProjectDirectorySql}, @Command, @UpdatedAt
            FROM sessions session_row
            LEFT JOIN workspaces workspace_row ON workspace_row.id = session_row.workspace_id
            WHERE session_row.id = @SessionId AND session_row.user_id = @UserId
            ON CONFLICT(user_id, project_directory) DO UPDATE SET
                command = excluded.command,
                updated_at = excluded.updated_at
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("Command", command);
                cmd.AddParameter("UpdatedAt", updatedAt);
            });
    }

    public async Task<string?> GetPreviewCommandAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            $"""
            SELECT preview.command
            FROM sessions session_row
            LEFT JOIN workspaces workspace_row ON workspace_row.id = session_row.workspace_id
            JOIN preview_commands preview
                ON preview.user_id = session_row.user_id AND preview.project_directory = {ProjectDirectorySql}
            WHERE session_row.id = @SessionId AND session_row.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            r => r.GetString(0));
    }

    private static AppRun ReadRun(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        SessionId = r.GetString(r.GetOrdinal("session_id")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        Command = r.GetString(r.GetOrdinal("command")),
        Directory = r.GetString(r.GetOrdinal("directory")),
        Port = (int)r.GetInt64(r.GetOrdinal("port")),
        Status = r.GetString(r.GetOrdinal("status")),
        ExitCode = r.GetNullableInt32(r.GetOrdinal("exit_code")),
        Url = r.GetNullableString(r.GetOrdinal("url")),
        Pid = r.GetNullableInt32(r.GetOrdinal("pid")),
        PidStartedAt = r.GetNullableString(r.GetOrdinal("pid_started_at")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
    };
}
