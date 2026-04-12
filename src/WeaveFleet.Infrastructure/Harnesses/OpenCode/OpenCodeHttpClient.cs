using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Typed HTTP client wrapping the full OpenCode API surface used by the harness.
/// Each harness instance owns one of these (not a singleton).
/// </summary>
internal sealed class OpenCodeHttpClient
{
    private static readonly Action<ILogger, string, Exception?> LogRequest =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, "Request"),
            "OpenCode API request: {RequestLine}");

    private static readonly Action<ILogger, int, string, Exception?> LogResponse =
        LoggerMessage.Define<int, string>(LogLevel.Debug, new EventId(2, "Response"),
            "OpenCode API response: {StatusCode} {Url}");

    private static readonly Action<ILogger, int, string, Exception?> LogRequestFailed =
        LoggerMessage.Define<int, string>(LogLevel.Warning, new EventId(3, "RequestFailed"),
            "OpenCode API call failed with status {StatusCode}: {Url}");

    private static readonly Action<ILogger, string, Exception?> LogSseHeartbeat =
        LoggerMessage.Define<string>(LogLevel.Trace, new EventId(4, "SseHeartbeat"),
            "SSE heartbeat or control event: {Type}");

    private static readonly Action<ILogger, int, Exception?> LogSseReconnect =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(5, "SseReconnect"),
            "SSE stream disconnected; reconnecting in {DelayMs}ms.");

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenCodeHttpClient> _logger;

    public OpenCodeHttpClient(HttpClient httpClient, ILogger<OpenCodeHttpClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Health
    // -----------------------------------------------------------------------

    /// <summary>GET /global/health</summary>
    public async Task<OpenCodeHealthResponse> CheckHealthAsync(CancellationToken ct)
    {
        return await GetAsync<OpenCodeHealthResponse>("/global/health", ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Sessions
    // -----------------------------------------------------------------------

    /// <summary>POST /session?directory={directory}</summary>
    public async Task<OpenCodeSessionInfo> CreateSessionAsync(
        OpenCodeCreateSessionRequest? request,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl("/session", directory);
        return await PostAsync<OpenCodeSessionInfo>(url, request, ct).ConfigureAwait(false);
    }

    /// <summary>GET /session/{sessionId}?directory={directory}</summary>
    public async Task<OpenCodeSessionInfo> GetSessionAsync(
        string sessionId,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}", directory);
        return await GetAsync<OpenCodeSessionInfo>(url, ct).ConfigureAwait(false);
    }

    /// <summary>GET /session?directory={directory}</summary>
    public async Task<IReadOnlyList<OpenCodeSessionInfo>> ListSessionsAsync(
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl("/session", directory);
        return await GetAsync<IReadOnlyList<OpenCodeSessionInfo>>(url, ct).ConfigureAwait(false)
               ?? [];
    }

    /// <summary>DELETE /session/{sessionId}?directory={directory}</summary>
    public async Task<bool> DeleteSessionAsync(
        string sessionId,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}", directory);
        using var response = await _httpClient.DeleteAsync(url, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);
        return response.IsSuccessStatusCode;
    }

    /// <summary>POST /session/{sessionId}/message?directory={directory}</summary>
    public async Task<OpenCodeMessageWithParts> SendMessageAsync(
        string sessionId,
        OpenCodePromptRequest request,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}/message", directory);
        return await PostAsync<OpenCodeMessageWithParts>(url, request, ct).ConfigureAwait(false);
    }

    /// <summary>POST /session/{sessionId}/prompt_async?directory={directory} — fire and forget (204).</summary>
    public async Task SendPromptAsyncFireAndForget(
        string sessionId,
        OpenCodePromptRequest request,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}/prompt_async", directory);
        LogRequest(_logger, $"POST {url}", null);

        var content = new StringContent(
            JsonSerializer.Serialize(request, OpenCodeJsonOptions.Default),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);

        if (!response.IsSuccessStatusCode)
        {
            LogRequestFailed(_logger, (int)response.StatusCode, url, null);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>POST /session/{sessionId}/command?directory={directory} — fire and forget.</summary>
    public async Task SendCommandAsync(
        string sessionId,
        OpenCodeCommandRequest request,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}/command", directory);
        var logUrl = RedactDirectory(url);
        LogRequest(_logger, $"POST {logUrl}", null);

        var content = new StringContent(
            JsonSerializer.Serialize(request, OpenCodeJsonOptions.Default),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, logUrl, null);

        if (!response.IsSuccessStatusCode)
        {
            LogRequestFailed(_logger, (int)response.StatusCode, logUrl, null);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>GET /session/{sessionId}/message?directory={directory}[&amp;limit=N][&amp;before=cursor]</summary>
    /// <remarks>
    /// Deserializes each message manually to work around a STJ polymorphism limitation:
    /// the <c>role</c> type discriminator on <see cref="OpenCodeMessageInfo"/> may not be the
    /// first property in the OpenCode API response, which causes STJ to fall back to the base
    /// type (Role = "unknown") instead of the concrete <see cref="OpenCodeUserMessage"/> or
    /// <see cref="OpenCodeAssistantMessage"/> subtypes. This mirrors the workaround in
    /// <see cref="OpenCodeHarnessSession.TryPersistMessageAsync"/>.
    /// </remarks>
    public async Task<IReadOnlyList<OpenCodeMessageWithParts>> GetMessagesAsync(
        string sessionId,
        string directory,
        int? limit,
        string? before,
        CancellationToken ct)
    {
        var sb = new StringBuilder($"/session/{Uri.EscapeDataString(sessionId)}/message?directory={Uri.EscapeDataString(directory)}");
        if (limit.HasValue) sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"&limit={limit.Value}");
        if (before is not null) sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"&before={Uri.EscapeDataString(before)}");

        var url = sb.ToString();
        LogRequest(_logger, $"GET {url}", null);

        using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);

        if (!response.IsSuccessStatusCode)
        {
            LogRequestFailed(_logger, (int)response.StatusCode, url, null);
            response.EnsureSuccessStatusCode();
        }

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        return DeserializeMessages(doc.RootElement);
    }

    /// <summary>
    /// Deserializes an array of OpenCode messages, manually dispatching on the <c>info.role</c>
    /// discriminator to avoid STJ polymorphism ordering issues.
    /// </summary>
    private static List<OpenCodeMessageWithParts> DeserializeMessages(JsonElement array)
    {
        var result = new List<OpenCodeMessageWithParts>();

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            if (!element.TryGetProperty("info", out var infoEl) || infoEl.ValueKind != JsonValueKind.Object)
                continue;

            // Read role from raw JSON — avoid polymorphic deserialization of the abstract base type,
            // which requires the discriminator to be the first property in STJ polymorphism.
            if (!infoEl.TryGetProperty("role", out var roleEl))
                continue;

            var role = roleEl.GetString();
            if (role is not ("user" or "assistant"))
                continue;

            OpenCodeMessageInfo? info = role == "assistant"
                ? infoEl.Deserialize<OpenCodeAssistantMessage>(OpenCodeJsonOptions.Default)
                : infoEl.Deserialize<OpenCodeUserMessage>(OpenCodeJsonOptions.Default);

            if (info is null)
                continue;

            var parts = element.TryGetProperty("parts", out var partsEl)
                ? DeserializeParts(partsEl)
                : [];

            result.Add(new OpenCodeMessageWithParts { Info = info, Parts = parts });
        }

        return result;
    }

    /// <summary>
    /// Deserializes an array of message parts, manually dispatching on the <c>type</c>
    /// discriminator to avoid STJ polymorphism ordering issues.
    /// Mirrors the pattern in <see cref="OpenCodeHarnessSession.TryPersistPartAsync"/>.
    /// </summary>
    internal static List<OpenCodeMessagePart> DeserializeParts(JsonElement partsArray)
    {
        if (partsArray.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<OpenCodeMessagePart>();

        foreach (var partEl in partsArray.EnumerateArray())
        {
            if (partEl.ValueKind != JsonValueKind.Object)
                continue;

            if (!partEl.TryGetProperty("type", out var typeEl))
                continue;

            OpenCodeMessagePart? part = typeEl.GetString() switch
            {
                "text" => partEl.Deserialize<OpenCodeTextPart>(OpenCodeJsonOptions.Default),
                "tool" => partEl.Deserialize<OpenCodeToolPart>(OpenCodeJsonOptions.Default),
                "reasoning" => partEl.Deserialize<OpenCodeReasoningPart>(OpenCodeJsonOptions.Default),
                "step-start" => partEl.Deserialize<OpenCodeStepStartPart>(OpenCodeJsonOptions.Default),
                "step-finish" => partEl.Deserialize<OpenCodeStepFinishPart>(OpenCodeJsonOptions.Default),
                "file" => partEl.Deserialize<OpenCodeFilePart>(OpenCodeJsonOptions.Default),
                "agent" => partEl.Deserialize<OpenCodeAgentPart>(OpenCodeJsonOptions.Default),
                "subtask" => partEl.Deserialize<OpenCodeSubtaskPart>(OpenCodeJsonOptions.Default),
                "snapshot" => partEl.Deserialize<OpenCodeSnapshotPart>(OpenCodeJsonOptions.Default),
                "patch" => partEl.Deserialize<OpenCodePatchPart>(OpenCodeJsonOptions.Default),
                "retry" => partEl.Deserialize<OpenCodeRetryPart>(OpenCodeJsonOptions.Default),
                "compaction" => partEl.Deserialize<OpenCodeCompactionPart>(OpenCodeJsonOptions.Default),
                _ => null,
            };

            if (part is not null)
                result.Add(part);
        }

        return result;
    }

    /// <summary>POST /session/{sessionId}/abort?directory={directory}</summary>
    public async Task<bool> AbortAsync(string sessionId, string directory, CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}/abort", directory);
        LogRequest(_logger, $"POST {url}", null);

        using var response = await _httpClient.PostAsync(url, null, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);
        return response.IsSuccessStatusCode;
    }

    /// <summary>POST /session/{sessionId}/fork?directory={directory}</summary>
    public async Task<OpenCodeSessionInfo> ForkSessionAsync(
        string sessionId,
        OpenCodeForkRequest request,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}/fork", directory);
        return await PostAsync<OpenCodeSessionInfo>(url, request, ct).ConfigureAwait(false);
    }

    /// <summary>GET /agent?directory={directory}</summary>
    public async Task<IReadOnlyList<OpenCodeAgentInfo>> GetAgentsAsync(string directory, CancellationToken ct)
    {
        var url = BuildUrl("/agent", directory);
        return await GetAsync<IReadOnlyList<OpenCodeAgentInfo>>(url, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>GET /command?directory={directory}</summary>
    public async Task<IReadOnlyList<OpenCodeCommandInfo>> GetCommandsAsync(string directory, CancellationToken ct)
    {
        var url = BuildUrl("/command", directory);
        return await GetAsync<IReadOnlyList<OpenCodeCommandInfo>>(url, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>GET /provider?directory={directory}</summary>
    public async Task<OpenCodeProvidersResponse> GetProvidersAsync(string directory, CancellationToken ct)
    {
        var url = BuildUrl("/provider", directory);
        return await GetAsync<OpenCodeProvidersResponse>(url, ct).ConfigureAwait(false);
    }

    /// <summary>GET /session/status?directory={directory}</summary>
    public async Task<Dictionary<string, OpenCodeSessionStatus>> GetSessionStatusAsync(
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl("/session/status", directory);
        return await GetAsync<Dictionary<string, OpenCodeSessionStatus>>(url, ct).ConfigureAwait(false)
               ?? [];
    }

    /// <summary>
    /// GET /event?directory={directory} — SSE stream.
    /// Reconnects on disconnect with exponential backoff until cancellation is requested.
    /// </summary>
    public async IAsyncEnumerable<OpenCodeSseEvent> SubscribeToEventsAsync(
        string directory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<OpenCodeSseEvent>(
            new BoundedChannelOptions(1000)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

        // Background task: reads SSE and writes to channel, reconnecting on errors.
        var producer = Task.Run(async () =>
        {
            int delayMs = 1000;
            const int maxDelayMs = 30_000;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var url = BuildUrl("/event", directory);
                    LogRequest(_logger, $"GET {url} (SSE)", null);

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    using var response = await _httpClient
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var reader = new System.IO.StreamReader(stream);

                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (line is null) break;

                        if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                        var json = line["data: ".Length..];
                        OpenCodeSseEvent? evt;
                        try
                        {
                            evt = JsonSerializer.Deserialize<OpenCodeSseEvent>(json, OpenCodeJsonOptions.Default);
                        }
                        catch (JsonException)
                        {
                            continue;
                        }

                        if (evt is null) continue;

                        if (evt.Type is "server.heartbeat" or "server.connected")
                        {
                            LogSseHeartbeat(_logger, evt.Type, null);
                            continue;
                        }

                        await channel.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
                    }

                    // Clean stream end — reconnect quickly
                    delayMs = 1000;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogSseReconnect(_logger, delayMs, ex);
                }

                if (!ct.IsCancellationRequested)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    delayMs = Math.Min(delayMs * 2, maxDelayMs);
                }
            }

            channel.Writer.Complete();
        }, ct);

        await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return evt;
        }

        await producer.ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        LogRequest(_logger, $"GET {url}", null);
        using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);

        if (!response.IsSuccessStatusCode)
        {
            LogRequestFailed(_logger, (int)response.StatusCode, url, null);
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content
            .ReadFromJsonAsync<T>(OpenCodeJsonOptions.Default, ct)
            .ConfigureAwait(false);

        return result!;
    }

    private async Task<T> PostAsync<T>(string url, object? body, CancellationToken ct)
    {
        LogRequest(_logger, $"POST {url}", null);

        StringContent? content = null;
        if (body is not null)
        {
            content = new StringContent(
                JsonSerializer.Serialize(body, body.GetType(), OpenCodeJsonOptions.Default),
                Encoding.UTF8,
                "application/json");
        }

        using var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);

        if (!response.IsSuccessStatusCode)
        {
            LogRequestFailed(_logger, (int)response.StatusCode, url, null);
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content
            .ReadFromJsonAsync<T>(OpenCodeJsonOptions.Default, ct)
            .ConfigureAwait(false);

        return result!;
    }

    private static string BuildUrl(string path, string directory)
    {
        char separator = path.Contains('?') ? '&' : '?';
        return $"{path}{separator}directory={Uri.EscapeDataString(directory)}";
    }

    /// <summary>Strips the <c>directory</c> query parameter value from a URL for safe logging.</summary>
    private static string RedactDirectory(string url)
    {
        const string marker = "directory=";
        var idx = url.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return url;
        var endIdx = url.IndexOf('&', idx);
        return endIdx < 0
            ? string.Concat(url.AsSpan(0, idx + marker.Length), "[redacted]")
            : string.Concat(url.AsSpan(0, idx + marker.Length), "[redacted]", url.AsSpan(endIdx));
    }
}
