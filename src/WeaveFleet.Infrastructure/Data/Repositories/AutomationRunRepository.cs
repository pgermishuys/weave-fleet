using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>
/// Automation runs. The latest run per automation is the current user's. The rest is read by automation id, from the
/// scheduler (which spans every user, like <see cref="AutomationRepository.ListEnabledByTriggerTypeAsync"/>) or after
/// the caller has loaded the automation as its owner.
/// </summary>
public sealed class AutomationRunRepository(IDbConnectionFactory connectionFactory, IUserContext userContext) : IAutomationRunRepository
{
    public async Task InsertAsync(AutomationRun run)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO automation_runs (
                id, automation_id, user_id, trigger, scheduled_for, started_at, status, session_id, instance_id, error,
                workflow_run_id
            ) VALUES (
                @Id, @AutomationId, @UserId, @Trigger, @ScheduledFor, @StartedAt, @Status, @SessionId, @InstanceId, @Error,
                @WorkflowRunId
            )
            """,
            cmd =>
            {
                cmd.AddParameter("Id", run.Id);
                cmd.AddParameter("AutomationId", run.AutomationId);
                cmd.AddParameter("UserId", run.UserId);
                cmd.AddParameter("Trigger", run.Trigger);
                cmd.AddParameter("ScheduledFor", run.ScheduledFor);
                cmd.AddParameter("StartedAt", run.StartedAt);
                cmd.AddParameter("Status", run.Status);
                cmd.AddParameter("SessionId", run.SessionId);
                cmd.AddParameter("InstanceId", run.InstanceId);
                cmd.AddParameter("Error", run.Error);
                cmd.AddParameter("WorkflowRunId", run.WorkflowRunId);
            });
    }

    public async Task CompleteAsync(string id, string status, string? sessionId, string? instanceId, string? reason, string? workflowRunId = null)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE automation_runs
            SET status = @Status, session_id = @SessionId, instance_id = @InstanceId, error = @Error,
                workflow_run_id = @WorkflowRunId
            WHERE id = @Id
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("Status", status);
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("InstanceId", instanceId);
                cmd.AddParameter("Error", reason);
                cmd.AddParameter("WorkflowRunId", workflowRunId);
            });
    }

    public async Task<IReadOnlyList<AutomationRun>> ListByAutomationAsync(string automationId, int limit)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT *
            FROM automation_runs
            WHERE automation_id = @AutomationId
            ORDER BY started_at DESC, id DESC
            LIMIT @Limit
            """,
            cmd =>
            {
                cmd.AddParameter("AutomationId", automationId);
                cmd.AddParameter("Limit", limit);
            },
            MapRun);
    }

    public async Task<IReadOnlyDictionary<string, AutomationRun>> GetLatestPerAutomationAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var runs = await conn.QueryAsync(
            """
            SELECT r.*
            FROM automation_runs r
            WHERE r.user_id = @UserId
              AND r.id = (
                SELECT latest.id FROM automation_runs latest
                WHERE latest.automation_id = r.automation_id
                ORDER BY latest.started_at DESC, latest.id DESC
                LIMIT 1)
            """,
            cmd => cmd.AddParameter("UserId", userContext.UserId),
            MapRun);
        return runs.ToDictionary(run => run.AutomationId, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetLastScheduledForAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync(
            """
            SELECT automation_id, MAX(scheduled_for) AS last_scheduled_for
            FROM automation_runs
            WHERE scheduled_for IS NOT NULL
            GROUP BY automation_id
            """,
            r => (Id: r.GetString(0), LastScheduledFor: r.GetString(1)));
        return rows.ToDictionary(row => row.Id, row => row.LastScheduledFor, StringComparer.Ordinal);
    }

    public async Task<int> FailStaleStartingAsync(string beforeUtc, string reason)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteNonQueryAsync(
            """
            UPDATE automation_runs
            SET status = 'failed', error = @Error
            WHERE status = 'starting' AND started_at < @Before
            """,
            cmd =>
            {
                cmd.AddParameter("Before", beforeUtc);
                cmd.AddParameter("Error", reason);
            });
    }

    private static AutomationRun MapRun(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        AutomationId = r.GetString(r.GetOrdinal("automation_id")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        Trigger = r.GetString(r.GetOrdinal("trigger")),
        ScheduledFor = r.GetNullableString(r.GetOrdinal("scheduled_for")),
        StartedAt = r.GetString(r.GetOrdinal("started_at")),
        Status = r.GetString(r.GetOrdinal("status")),
        SessionId = r.GetNullableString(r.GetOrdinal("session_id")),
        InstanceId = r.GetNullableString(r.GetOrdinal("instance_id")),
        Error = r.GetNullableString(r.GetOrdinal("error")),
        WorkflowRunId = r.GetNullableString(r.GetOrdinal("workflow_run_id")),
    };
}
