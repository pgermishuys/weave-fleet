using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// The parts of OpenCode 2's server API (<c>/api/…</c>) Fleet uses. One per server; the caller sets the base
/// address and Basic auth on both clients.
/// </summary>
/// <param name="http">For requests; has a timeout.</param>
/// <param name="events">For the event stream; no timeout, since the stream stays open.</param>
internal sealed partial class OpenCode2HttpClient(HttpClient http, HttpClient events, ILogger<OpenCode2HttpClient> logger) : IDisposable
{
    public Task<OpenCode2ServerInfo?> GetInfoAsync(CancellationToken ct)
        => http.GetFromJsonAsync("api/info", OpenCode2JsonContext.Default.OpenCode2ServerInfo, ct);

    public async Task<OpenCode2SessionInfo> CreateSessionAsync(string directory, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            "api/session",
            new OpenCode2CreateSessionRequest { Location = new OpenCode2Location { Directory = directory } },
            OpenCode2JsonContext.Default.OpenCode2CreateSessionRequest,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "create a session", ct).ConfigureAwait(false);

        var body = await response.Content.ReadFromJsonAsync(
            OpenCode2JsonContext.Default.OpenCode2EnvelopeOpenCode2SessionInfo, ct).ConfigureAwait(false);
        return body?.Data is { Id: { Length: > 0 } } session
            ? session
            : throw new InvalidOperationException("OpenCode 2 created a session but didn't say its id.");
    }

    /// <summary>The session, or <see langword="null"/> when this server's database has no session <paramref name="sessionId"/>.</summary>
    public async Task<OpenCode2SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/session/{Uri.EscapeDataString(sessionId)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "read the session", ct).ConfigureAwait(false);

        var body = await response.Content.ReadFromJsonAsync(
            OpenCode2JsonContext.Default.OpenCode2EnvelopeOpenCode2SessionInfo, ct).ConfigureAwait(false);
        return body?.Data;
    }

    public async Task PromptAsync(string sessionId, string text, string? messageId, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/prompt",
            new OpenCode2PromptRequest { Id = messageId, Text = text },
            OpenCode2JsonContext.Default.OpenCode2PromptRequest,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "send the prompt", ct).ConfigureAwait(false);
    }

    public async Task InterruptAsync(string sessionId, CancellationToken ct)
    {
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/interrupt", content, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "interrupt the session", ct).ConfigureAwait(false);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        using var response = await http.DeleteAsync($"api/session/{Uri.EscapeDataString(sessionId)}", ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, "delete the session", ct).ConfigureAwait(false);
    }

    /// <summary>The ids of the sessions with a turn running on this server.</summary>
    public async Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(CancellationToken ct)
    {
        var body = await http.GetFromJsonAsync(
            "api/session/active",
            OpenCode2JsonContext.Default.OpenCode2EnvelopeDictionaryStringJsonElement,
            ct).ConfigureAwait(false);
        return body?.Data is { } active
            ? active.Keys.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads <c>GET /api/event</c> until it ends. <paramref name="connected"/> runs once the server has accepted the
    /// subscription, before any event is read. The stream is live only: V2 doesn't replay what was missed.
    /// </summary>
    public async IAsyncEnumerable<OpenCode2Event> ReadEventsAsync(
        Action connected,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/event");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await events.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "subscribe to events", ct).ConfigureAwait(false);
        connected();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var data = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            // An event is "data:" lines up to a blank line; lines starting with ':' are heartbeats.
            if (line.Length == 0)
            {
                if (data.Length > 0 && Parse(data.ToString()) is { } evt)
                    yield return evt;
                data.Clear();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
        }
    }

    /// <summary>One event's JSON, or <see langword="null"/> when it isn't an event Fleet can read.</summary>
    internal OpenCode2Event? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, OpenCode2JsonContext.Default.OpenCode2Event);
        }
        catch (JsonException ex)
        {
            LogUnreadableEvent(logger, ex);
            return null;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (body.Length > 500)
            body = body[..500];
        throw new HttpRequestException(
            $"OpenCode 2 couldn't {action}: {(int)response.StatusCode} {response.ReasonPhrase}. {body}".TrimEnd(),
            inner: null,
            response.StatusCode);
    }

    public void Dispose()
    {
        http.Dispose();
        events.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped an OpenCode 2 event Fleet couldn't read")]
    private static partial void LogUnreadableEvent(ILogger logger, Exception exception);
}
