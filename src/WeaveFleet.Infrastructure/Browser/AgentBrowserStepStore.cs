using System.Globalization;
using System.Text.Json;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Data;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// Keeps each session's browser steps (<c>agent_browser_steps</c>, gone with the session) and pushes each new one to
/// the session's watchers as <c>browser.step</c>.
/// </summary>
public sealed class AgentBrowserStepStore(IDbConnectionFactory connections, IEventBroadcaster events) : IAgentBrowserSteps, IDisposable
{
    // One Fleet writes these; numbering a session's steps one at a time keeps seq gapless without a transaction.
    private readonly SemaphoreSlim _write = new(1, 1);

    public async Task<AgentBrowserStep> RecordAsync(AgentBrowserStep entry, string userId, CancellationToken ct = default)
    {
        AgentBrowserStep recorded;
        await _write.WaitAsync(ct);
        try
        {
            using var connection = connections.CreateConnection();
            var last = await connection.ExecuteScalarAsync<long>(
                "SELECT COALESCE(MAX(seq), 0) FROM agent_browser_steps WHERE session_id = @SessionId",
                cmd => cmd.AddParameter("@SessionId", entry.SessionId)).ConfigureAwait(false);
            recorded = entry with { Seq = last + 1 };
            await connection.ExecuteNonQueryAsync(
                "INSERT INTO agent_browser_steps (session_id, seq, at, step_json) VALUES (@SessionId, @Seq, @At, @Json)",
                cmd =>
                {
                    cmd.AddParameter("@SessionId", recorded.SessionId);
                    cmd.AddParameter("@Seq", recorded.Seq);
                    cmd.AddParameter("@At", recorded.At.ToString("O", CultureInfo.InvariantCulture));
                    cmd.AddParameter("@Json", JsonSerializer.Serialize(ToWire(recorded), InfrastructureJsonContext.Default.BrowserStep));
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }

        var wire = ToWire(recorded);
        await events.BroadcastAsync(
            $"session:{recorded.SessionId}",
            "browser.step",
            JsonSerializer.SerializeToElement(wire, InfrastructureJsonContext.Default.BrowserStep),
            new BrowserStepped { Payload = wire },
            userId,
            ct).ConfigureAwait(false);
        return recorded;
    }

    public async Task<IReadOnlyList<AgentBrowserStep>> ListAsync(string sessionId, int limit = 500, CancellationToken ct = default)
    {
        using var connection = connections.CreateConnection();
        var rows = await connection.QueryAsync(
            """
            SELECT step_json FROM (
              SELECT seq, step_json FROM agent_browser_steps WHERE session_id = @SessionId ORDER BY seq DESC LIMIT @Limit
            ) ORDER BY seq
            """,
            cmd =>
            {
                cmd.AddParameter("@SessionId", sessionId);
                cmd.AddParameter("@Limit", Math.Clamp(limit, 1, 5000));
            },
            reader => reader.GetString(0)).ConfigureAwait(false);
        return [.. rows
            .Select(json => JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.BrowserStep))
            .OfType<BrowserStep>()
            .Select(FromWire)];
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var connection = connections.CreateConnection();
        await connection.ExecuteNonQueryAsync(
            "DELETE FROM agent_browser_steps WHERE session_id = @SessionId",
            cmd => cmd.AddParameter("@SessionId", sessionId),
            ct).ConfigureAwait(false);
    }

    public void Dispose() => _write.Dispose();

    /// <summary>The step in the shape clients get and the table keeps.</summary>
    public static BrowserStep ToWire(AgentBrowserStep step) => new()
    {
        SessionId = step.SessionId,
        Seq = step.Seq,
        At = step.At,
        Kind = step.Kind,
        Summary = step.Summary,
        Detail = step.Detail,
        Ok = step.Ok,
        Error = step.Error,
        TabId = step.TabId,
        Url = step.Url,
        Title = step.Title,
        Box = step.Box is { } box ? new BrowserBox(box.X, box.Y, box.Width, box.Height) : null,
        Screenshot = step.Screenshot is { } shot ? new BrowserShot(shot.Id, shot.Width, shot.Height) : null,
    };

    private static AgentBrowserStep FromWire(BrowserStep step) => new()
    {
        SessionId = step.SessionId,
        Seq = step.Seq,
        At = step.At,
        Kind = step.Kind,
        Summary = step.Summary,
        Detail = step.Detail,
        Ok = step.Ok,
        Error = step.Error,
        TabId = step.TabId,
        Url = step.Url,
        Title = step.Title,
        Box = step.Box is { } box ? new AgentBox(box.X, box.Y, box.Width, box.Height) : null,
        Screenshot = step.Screenshot is { } shot ? new ScreenshotReference(step.SessionId, shot.Id, shot.Width, shot.Height) : null,
    };
}
