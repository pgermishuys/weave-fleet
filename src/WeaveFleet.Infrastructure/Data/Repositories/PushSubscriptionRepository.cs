using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>The <c>push_subscriptions</c> table (migration 052).</summary>
public sealed class PushSubscriptionRepository(IDbConnectionFactory connectionFactory) : IPushSubscriptionRepository
{
    private const string Columns = "id, device_id, channel, endpoint, p256dh, auth, kinds, quiet_when_desk, user_agent, created_at, last_success_at, failure_count";

    public async Task<IReadOnlyList<PushSubscriptionRecord>> ListAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync($"SELECT {Columns} FROM push_subscriptions ORDER BY created_at, id", _ => { }, Read);
    }

    public async Task<PushSubscriptionRecord?> GetByEndpointAsync(string endpoint)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            $"SELECT {Columns} FROM push_subscriptions WHERE endpoint = @Endpoint",
            cmd => cmd.AddParameter("Endpoint", endpoint),
            Read);
    }

    public async Task<PushSubscriptionRecord> UpsertAsync(PushSubscriptionRecord subscription)
    {
        using var conn = connectionFactory.CreateConnection();
        var saved = await conn.QueryFirstOrDefaultAsync(
            $"""
            INSERT INTO push_subscriptions (id, device_id, channel, endpoint, p256dh, auth, kinds, quiet_when_desk, user_agent, created_at, last_success_at, failure_count)
            VALUES (@Id, @DeviceId, @Channel, @Endpoint, @P256dh, @Auth, @Kinds, @QuietWhenDesk, @UserAgent, @CreatedAt, NULL, 0)
            ON CONFLICT (endpoint) DO UPDATE SET
              device_id = excluded.device_id,
              channel = excluded.channel,
              p256dh = excluded.p256dh,
              auth = excluded.auth,
              kinds = excluded.kinds,
              quiet_when_desk = excluded.quiet_when_desk,
              user_agent = excluded.user_agent,
              failure_count = 0
            RETURNING {Columns}
            """,
            cmd =>
            {
                cmd.AddParameter("Id", subscription.Id);
                cmd.AddParameter("DeviceId", subscription.DeviceId);
                cmd.AddParameter("Channel", subscription.Channel);
                cmd.AddParameter("Endpoint", subscription.Endpoint);
                cmd.AddParameter("P256dh", subscription.P256dh);
                cmd.AddParameter("Auth", subscription.Auth);
                cmd.AddParameter("Kinds", WriteKinds(subscription.Kinds));
                cmd.AddParameter("QuietWhenDesk", subscription.QuietWhenDesk ? 1 : 0);
                cmd.AddParameter("UserAgent", subscription.UserAgent);
                cmd.AddParameter("CreatedAt", Format(subscription.CreatedAt));
            },
            Read);
        return saved!;
    }

    public async Task<bool> DeleteByEndpointAsync(string endpoint)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteNonQueryAsync(
            "DELETE FROM push_subscriptions WHERE endpoint = @Endpoint",
            cmd => cmd.AddParameter("Endpoint", endpoint)) > 0;
    }

    public async Task<int> DeleteByDeviceAsync(string deviceId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteNonQueryAsync(
            "DELETE FROM push_subscriptions WHERE device_id = @DeviceId",
            cmd => cmd.AddParameter("DeviceId", deviceId));
    }

    public async Task RecordSuccessAsync(string id, DateTimeOffset at)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE push_subscriptions SET last_success_at = @At, failure_count = 0 WHERE id = @Id",
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("At", Format(at));
            });
    }

    public async Task<int> RecordFailureAsync(string id)
    {
        using var conn = connectionFactory.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync(
            "UPDATE push_subscriptions SET failure_count = failure_count + 1 WHERE id = @Id RETURNING failure_count",
            cmd => cmd.AddParameter("Id", id),
            r => new FailureCount(r.GetInt32(0)));
        return row?.Count ?? 0;
    }

    private sealed record FailureCount(int Count);

    private static string WriteKinds(IReadOnlyList<string> kinds)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var kind in kinds)
                writer.WriteStringValue(kind);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static List<string> ReadKinds(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static PushSubscriptionRecord Read(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        DeviceId = r.GetNullableString(r.GetOrdinal("device_id")),
        Channel = r.GetString(r.GetOrdinal("channel")),
        Endpoint = r.GetString(r.GetOrdinal("endpoint")),
        P256dh = r.GetString(r.GetOrdinal("p256dh")),
        Auth = r.GetString(r.GetOrdinal("auth")),
        Kinds = ReadKinds(r.GetString(r.GetOrdinal("kinds"))),
        QuietWhenDesk = r.GetInt64(r.GetOrdinal("quiet_when_desk")) != 0,
        UserAgent = r.GetNullableString(r.GetOrdinal("user_agent")),
        CreatedAt = Parse(r.GetString(r.GetOrdinal("created_at"))),
        LastSuccessAt = r.GetNullableString(r.GetOrdinal("last_success_at")) is { } at ? Parse(at) : null,
        FailureCount = r.GetInt32(r.GetOrdinal("failure_count")),
    };
}
