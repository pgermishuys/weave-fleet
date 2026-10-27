using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>The <c>jobs</c> and <c>job_machine_settings</c> tables (migration 068).</summary>
public sealed class JobRepository(IDbConnectionFactory connectionFactory) : IJobRepository
{
    private const string Columns = "id, user_id, title, ask, repository, labels, pinned_machine_id, state, machine_id, machine_name, session_id, worked, error, created_at, updated_at";

    public async Task InsertAsync(Job job)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            $"""
            INSERT INTO jobs ({Columns})
            VALUES (@Id, @UserId, @Title, @Ask, @Repository, @Labels, @PinnedMachineId, @State, @MachineId, @MachineName, @SessionId, @Worked, @Error, @CreatedAt, @UpdatedAt)
            """,
            cmd =>
            {
                cmd.AddParameter("Id", job.Id);
                cmd.AddParameter("UserId", job.UserId);
                cmd.AddParameter("Title", job.Title);
                cmd.AddParameter("Ask", job.Ask);
                cmd.AddParameter("Repository", job.Repository);
                cmd.AddParameter("Labels", SerializeLabels(job.Labels));
                cmd.AddParameter("PinnedMachineId", job.PinnedMachineId);
                cmd.AddParameter("State", job.State);
                cmd.AddParameter("MachineId", job.MachineId);
                cmd.AddParameter("MachineName", job.MachineName);
                cmd.AddParameter("SessionId", job.SessionId);
                cmd.AddParameter("Worked", job.Worked ? 1 : 0);
                cmd.AddParameter("Error", job.Error);
                cmd.AddParameter("CreatedAt", Format(job.CreatedAt));
                cmd.AddParameter("UpdatedAt", Format(job.UpdatedAt));
            });
    }

    public async Task<IReadOnlyList<Job>> ListAsync(string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            $"SELECT {Columns} FROM jobs WHERE user_id = @UserId ORDER BY created_at, id",
            cmd => cmd.AddParameter("UserId", userId),
            ReadJob);
    }

    public async Task<IReadOnlyList<string>> ListUsersWithOpenJobsAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT DISTINCT user_id FROM jobs WHERE state IN ('waiting', 'running', 'needs_you')",
            _ => { },
            r => r.GetString(0));
    }

    public async Task<bool> ClaimAsync(string id, string machineId, string machineName)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteNonQueryAsync(
            "UPDATE jobs SET state = 'running', machine_id = @MachineId, machine_name = @MachineName, error = NULL, updated_at = @Now WHERE id = @Id AND state = 'waiting'",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("MachineId", machineId);
                cmd.AddParameter("MachineName", machineName);
                cmd.AddParameter("Now", Format(DateTimeOffset.UtcNow));
            }) > 0;
    }

    public async Task SetSessionAsync(string id, string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE jobs SET session_id = @SessionId, updated_at = @Now WHERE id = @Id",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("Now", Format(DateTimeOffset.UtcNow));
            });
    }

    public async Task ReleaseAsync(string id, string? reason)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE jobs SET state = 'waiting', machine_id = NULL, machine_name = NULL, session_id = NULL, worked = 0, error = @Error, updated_at = @Now WHERE id = @Id",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("Error", reason);
                cmd.AddParameter("Now", Format(DateTimeOffset.UtcNow));
            });
    }

    public async Task SetStateAsync(string id, string state, bool worked, string? reason)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE jobs SET state = @State, worked = @Worked, error = @Error, updated_at = @Now WHERE id = @Id",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("State", state);
                cmd.AddParameter("Worked", worked ? 1 : 0);
                cmd.AddParameter("Error", reason);
                cmd.AddParameter("Now", Format(DateTimeOffset.UtcNow));
            });
    }

    public async Task<bool> DeleteWaitingAsync(string id, string userId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteNonQueryAsync(
            "DELETE FROM jobs WHERE id = @Id AND user_id = @UserId AND state = 'waiting'",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UserId", userId);
            }) > 0;
    }

    public async Task<IReadOnlyList<JobMachineSettings>> ListMachineSettingsAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            "SELECT machine_id, seats, labels FROM job_machine_settings",
            _ => { },
            r => new JobMachineSettings(r.GetString(0), (int)r.GetInt64(1), DeserializeLabels(r.GetString(2))));
    }

    public async Task SaveMachineSettingsAsync(JobMachineSettings settings)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO job_machine_settings (machine_id, seats, labels) VALUES (@MachineId, @Seats, @Labels)
            ON CONFLICT (machine_id) DO UPDATE SET seats = excluded.seats, labels = excluded.labels
            """,
            cmd =>
            {
                cmd.AddParameter("MachineId", settings.MachineId);
                cmd.AddParameter("Seats", settings.Seats);
                cmd.AddParameter("Labels", SerializeLabels(settings.Labels));
            });
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string SerializeLabels(IReadOnlyList<string> labels) =>
        JsonSerializer.Serialize(labels.ToList(), InfrastructureJsonContext.Default.ListString);

    private static List<string> DeserializeLabels(string json) =>
        JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.ListString) ?? [];

    private static Job ReadJob(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        Title = r.GetString(r.GetOrdinal("title")),
        Ask = r.GetString(r.GetOrdinal("ask")),
        Repository = r.GetString(r.GetOrdinal("repository")),
        Labels = DeserializeLabels(r.GetString(r.GetOrdinal("labels"))),
        PinnedMachineId = r.GetNullableString(r.GetOrdinal("pinned_machine_id")),
        State = r.GetString(r.GetOrdinal("state")),
        MachineId = r.GetNullableString(r.GetOrdinal("machine_id")),
        MachineName = r.GetNullableString(r.GetOrdinal("machine_name")),
        SessionId = r.GetNullableString(r.GetOrdinal("session_id")),
        Worked = r.GetInt64(r.GetOrdinal("worked")) != 0,
        Error = r.GetNullableString(r.GetOrdinal("error")),
        CreatedAt = Parse(r.GetString(r.GetOrdinal("created_at"))),
        UpdatedAt = Parse(r.GetString(r.GetOrdinal("updated_at"))),
    };
}
