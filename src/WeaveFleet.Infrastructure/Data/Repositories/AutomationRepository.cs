using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class AutomationRepository : IAutomationRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IUserContext _userContext;

    public AutomationRepository(IDbConnectionFactory connectionFactory, IUserContext userContext)
    {
        _connectionFactory = connectionFactory;
        _userContext = userContext;
    }

    public async Task InsertAsync(Automation automation)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO automations (
                id, name, prompt, trigger_type, trigger_config,
                max_concurrent_runs, max_runs_per_hour, timeout_minutes,
                is_enabled, is_deleted, workspace_id, model, agent,
                created_at, updated_at, user_id
            ) VALUES (
                @Id, @Name, @Prompt, @TriggerType, @TriggerConfig,
                @MaxConcurrentRuns, @MaxRunsPerHour, @TimeoutMinutes,
                @IsEnabled, @IsDeleted, @WorkspaceId, @Model, @Agent,
                @CreatedAt, @UpdatedAt, @UserId
            )
            """,
            cmd =>
            {
                cmd.AddParameter("Id", automation.Id);
                cmd.AddParameter("Name", automation.Name);
                cmd.AddParameter("Prompt", automation.Prompt);
                cmd.AddParameter("TriggerType", automation.TriggerType);
                cmd.AddParameter("TriggerConfig", automation.TriggerConfig);
                cmd.AddParameter("MaxConcurrentRuns", automation.MaxConcurrentRuns);
                cmd.AddParameter("MaxRunsPerHour", automation.MaxRunsPerHour);
                cmd.AddParameter("TimeoutMinutes", automation.TimeoutMinutes);
                cmd.AddParameter("IsEnabled", automation.IsEnabled ? 1 : 0);
                cmd.AddParameter("IsDeleted", automation.IsDeleted ? 1 : 0);
                cmd.AddParameter("WorkspaceId", automation.WorkspaceId);
                cmd.AddParameter("Model", automation.Model);
                cmd.AddParameter("Agent", automation.Agent);
                cmd.AddParameter("CreatedAt", automation.CreatedAt);
                cmd.AddParameter("UpdatedAt", automation.UpdatedAt);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task UpdateAsync(Automation automation)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE automations
            SET name = @Name,
                prompt = @Prompt,
                trigger_type = @TriggerType,
                trigger_config = @TriggerConfig,
                max_concurrent_runs = @MaxConcurrentRuns,
                max_runs_per_hour = @MaxRunsPerHour,
                timeout_minutes = @TimeoutMinutes,
                workspace_id = @WorkspaceId,
                model = @Model,
                agent = @Agent,
                updated_at = @UpdatedAt
            WHERE id = @Id AND user_id = @UserId AND is_deleted = 0
            """,
            cmd =>
            {
                cmd.AddParameter("Id", automation.Id);
                cmd.AddParameter("Name", automation.Name);
                cmd.AddParameter("Prompt", automation.Prompt);
                cmd.AddParameter("TriggerType", automation.TriggerType);
                cmd.AddParameter("TriggerConfig", automation.TriggerConfig);
                cmd.AddParameter("MaxConcurrentRuns", automation.MaxConcurrentRuns);
                cmd.AddParameter("MaxRunsPerHour", automation.MaxRunsPerHour);
                cmd.AddParameter("TimeoutMinutes", automation.TimeoutMinutes);
                cmd.AddParameter("WorkspaceId", automation.WorkspaceId);
                cmd.AddParameter("Model", automation.Model);
                cmd.AddParameter("Agent", automation.Agent);
                cmd.AddParameter("UpdatedAt", automation.UpdatedAt);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task<Automation?> GetByIdAsync(string id)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            """
            SELECT *
            FROM automations
            WHERE id = @Id AND user_id = @UserId AND is_deleted = 0
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            MapAutomation);
    }

    public async Task<IReadOnlyList<Automation>> ListAsync(string? workspaceId = null)
    {
        using var conn = _connectionFactory.CreateConnection();
        
        if (workspaceId is null)
        {
            return await conn.QueryAsync(
                """
                SELECT *
                FROM automations
                WHERE user_id = @UserId AND is_deleted = 0
                ORDER BY created_at DESC
                """,
                cmd => cmd.AddParameter("UserId", _userContext.UserId),
                MapAutomation);
        }
        
        return await conn.QueryAsync(
            """
            SELECT *
            FROM automations
            WHERE user_id = @UserId AND workspace_id = @WorkspaceId AND is_deleted = 0
            ORDER BY created_at DESC
            """,
            cmd =>
            {
                cmd.AddParameter("UserId", _userContext.UserId);
                cmd.AddParameter("WorkspaceId", workspaceId);
            },
            MapAutomation);
    }

    public async Task<IReadOnlyList<Automation>> ListEnabledByTriggerTypeAsync(string triggerType)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT *
            FROM automations
            WHERE trigger_type = @TriggerType AND is_enabled = 1 AND is_deleted = 0
            ORDER BY created_at DESC
            """,
            cmd => cmd.AddParameter("TriggerType", triggerType),
            MapAutomation);
    }

    public async Task DeleteAsync(string id)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE automations
            SET is_deleted = 1, updated_at = @UpdatedAt
            WHERE id = @Id AND user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UpdatedAt", DateTime.UtcNow.ToString("O"));
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE automations
            SET is_enabled = @IsEnabled, updated_at = @UpdatedAt
            WHERE id = @Id AND user_id = @UserId AND is_deleted = 0
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("IsEnabled", enabled ? 1 : 0);
                cmd.AddParameter("UpdatedAt", DateTime.UtcNow.ToString("O"));
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    private static Automation MapAutomation(DbDataReader r)
    {
        var workspaceIdOrd = r.GetOrdinal("workspace_id");
        var modelOrd = r.GetOrdinal("model");
        var agentOrd = r.GetOrdinal("agent");
        var updatedAtOrd = r.GetOrdinal("updated_at");

        return new Automation
        {
            Id = r.GetString(r.GetOrdinal("id")),
            Name = r.GetString(r.GetOrdinal("name")),
            Prompt = r.GetString(r.GetOrdinal("prompt")),
            TriggerType = r.GetString(r.GetOrdinal("trigger_type")),
            TriggerConfig = r.GetString(r.GetOrdinal("trigger_config")),
            MaxConcurrentRuns = r.GetInt32(r.GetOrdinal("max_concurrent_runs")),
            MaxRunsPerHour = r.GetInt32(r.GetOrdinal("max_runs_per_hour")),
            TimeoutMinutes = r.GetInt32(r.GetOrdinal("timeout_minutes")),
            IsEnabled = r.GetInt32(r.GetOrdinal("is_enabled")) != 0,
            IsDeleted = r.GetInt32(r.GetOrdinal("is_deleted")) != 0,
            WorkspaceId = r.GetNullableString(workspaceIdOrd),
            Model = r.GetNullableString(modelOrd),
            Agent = r.GetNullableString(agentOrd),
            CreatedAt = r.GetString(r.GetOrdinal("created_at")),
            UpdatedAt = r.GetNullableString(updatedAtOrd),
            UserId = r.GetString(r.GetOrdinal("user_id")),
        };
    }
}
