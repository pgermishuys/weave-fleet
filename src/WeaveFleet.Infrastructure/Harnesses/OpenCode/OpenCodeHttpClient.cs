using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

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

    private static readonly Action<ILogger, string, Exception?> LogRequestBody =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(6, "RequestBody"),
            "OpenCode API request body: {Body}");

    private static readonly Action<ILogger, string, Exception?> LogResponseBody =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(7, "ResponseBody"),
            "OpenCode API error response body: {Body}");

    private static readonly Action<ILogger, string, string?, Exception?> LogDirectoryScopeRejected =
        LoggerMessage.Define<string, string?>(LogLevel.Warning, new EventId(8, "DirectoryScopeRejected"),
            "Rejected OpenCode API request because directory scope does not match expected working directory. Expected directory: {ExpectedDirectory}; actual directory: {ActualDirectory}.");

    private static readonly Action<ILogger, string, Exception?> LogSseHeartbeat =
        LoggerMessage.Define<string>(LogLevel.Trace, new EventId(4, "SseHeartbeat"),
            "SSE heartbeat or control event: {Type}");

    private static readonly Action<ILogger, int, Exception?> LogSseReconnect =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(5, "SseReconnect"),
            "SSE stream disconnected; reconnecting in {DelayMs}ms.");

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenCodeHttpClient> _logger;
    private readonly string? _expectedDirectory;

    public OpenCodeHttpClient(HttpClient httpClient, ILogger<OpenCodeHttpClient> logger)
        : this(httpClient, logger, expectedDirectory: null)
    {
    }

    private OpenCodeHttpClient(
        HttpClient httpClient,
        ILogger<OpenCodeHttpClient> logger,
        string? expectedDirectory)
    {
        _httpClient = httpClient;
        _logger = logger;
        _expectedDirectory = expectedDirectory;
    }

    internal OpenCodeHttpClient WithExpectedDirectory(string expectedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDirectory);
        return new OpenCodeHttpClient(_httpClient, _logger, expectedDirectory);
    }

    // -----------------------------------------------------------------------
    // Health
    // -----------------------------------------------------------------------

    /// <summary>GET /global/health</summary>
    public async Task<OpenCodeHealthResponse> CheckHealthAsync(CancellationToken ct)
    {
        return await GetAsync("/global/health", OpenCodeJsonContext.Default.OpenCodeHealthResponse, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /config?directory={directory}. OpenCode reads a folder's config on the first request that names it, so
    /// this is where a broken config shows up: a 400 with <c>ConfigJsonError</c> (bad JSON) or
    /// <c>ConfigInvalidError</c> (a value of the wrong shape).
    /// </summary>
    public async Task<HarnessProfileCheck> CheckConfigAsync(string directory, CancellationToken ct)
    {
        var url = BuildUrl("/config", directory);
        ValidateDirectoryScope(url);
        LogRequest(_logger, $"GET {url}", null);
        using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return HarnessProfileCheck.Passed;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseConfigError((int)response.StatusCode, body);
    }

    internal static HarnessProfileCheck ParseConfigError(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;

            if (name == "ConfigInvalidError" && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
            {
                var lines = issues.EnumerateArray().Select(issue =>
                {
                    var path = issue.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.Array
                        ? string.Join('.', p.EnumerateArray().Select(segment => segment.ToString()))
                        : string.Empty;
                    var message = issue.TryGetProperty("message", out var m) ? m.GetString() : null;
                    return path.Length > 0 ? $"{path}: {message}" : message ?? string.Empty;
                }).Where(line => line.Length > 0).ToList();
                return new HarnessProfileCheck(false, "OpenCode rejected a value in this profile.", lines);
            }

            if (name == "ConfigJsonError" && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("message", out var jsonMessage) && jsonMessage.GetString() is { } text)
            {
                // "--- Errors ---\nEndOfFileExpected at line 2, column 1\n   Line 2: { oops\n          ^\n--- End ---"
                var errors = text.Split("--- Errors ---", 2) is [_, var rest] ? rest.Split("--- End ---")[0] : text;
                var lines = errors.Split('\n').Select(line => line.TrimEnd()).Where(line => line.Trim().Length > 0).ToList();
                return new HarnessProfileCheck(false, "This profile isn't valid JSON.", lines);
            }

            var fallback = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
            return new HarnessProfileCheck(false, $"OpenCode couldn't load this profile ({name ?? $"HTTP {status}"}).",
                fallback is null ? null : [fallback]);
        }
        catch (JsonException)
        {
            return new HarnessProfileCheck(false, $"OpenCode couldn't load this profile (HTTP {status}).");
        }
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
        var identity = await PostAsync(url, request, OpenCodeJsonContext.Default.OpenCodeCreateSessionRequest, OpenCodeJsonContext.Default.OpenCodeSessionIdentity, ct).ConfigureAwait(false);
        return new OpenCodeSessionInfo { Id = identity.Id };
    }

    /// <summary>GET /session/{sessionId}?directory={directory}</summary>
    public async Task<OpenCodeSessionInfo> GetSessionAsync(
        string sessionId,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}", directory);
        var identity = await GetAsync(url, OpenCodeJsonContext.Default.OpenCodeSessionIdentity, ct).ConfigureAwait(false);
        return new OpenCodeSessionInfo { Id = identity.Id };
    }

    /// <summary>GET /session/{sessionId}?directory={directory}, reading only the parent session id.</summary>
    public async Task<string?> GetSessionParentIdAsync(
        string sessionId,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}", directory);
        var session = await GetAsync(url, OpenCodeJsonContext.Default.OpenCodeSessionParent, ct).ConfigureAwait(false);
        return session.ParentId;
    }

    /// <summary>GET /session/{sessionId}/todo?directory={directory}</summary>
    public async Task<IReadOnlyList<OpenCodeTodo>> GetTodosAsync(
        string sessionId,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}/todo", directory);
        return await GetAsync(url, OpenCodeJsonContext.Default.ListOpenCodeTodo, ct).ConfigureAwait(false)
               ?? [];
    }

    /// <summary>GET /session?directory={directory}</summary>
    public async Task<IReadOnlyList<OpenCodeSessionInfo>> ListSessionsAsync(
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl("/session", directory);
        return await GetAsync(url, OpenCodeJsonContext.Default.ListOpenCodeSessionInfo, ct).ConfigureAwait(false)
               ?? [];
    }

    /// <summary>DELETE /session/{sessionId}?directory={directory}</summary>
    public async Task<bool> DeleteSessionAsync(
        string sessionId,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}", directory);
        ValidateDirectoryScope(url);
        using var response = await _httpClient.DeleteAsync(url, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);
        return response.IsSuccessStatusCode;
    }

    /// <summary>PATCH /session/{sessionId}?directory={directory}</summary>
    public async Task UpdateSessionAsync(
        string sessionId,
        OpenCodeSessionUpdateRequest request,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}", directory);
        ValidateDirectoryScope(url);
        var requestBody = JsonSerializer.Serialize(request, OpenCodeJsonContext.Default.OpenCodeSessionUpdateRequest);
        LogRequest(_logger, $"PATCH {url}", null);
        LogRequestBody(_logger, requestBody, null);

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PatchAsync(url, content, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);

        if (!response.IsSuccessStatusCode)
        {
            LogRequestFailed(_logger, (int)response.StatusCode, url, null);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>POST /session/{sessionId}/message?directory={directory} — waits for the reply.</summary>
    /// <remarks>
    /// Parsed by hand, like <see cref="GetMessagesAsync"/>: the reply's <c>role</c> and part <c>type</c>
    /// discriminators may not come first. Returns null when the reply isn't a user or assistant message.
    /// </remarks>
    public async Task<OpenCodeMessageWithParts?> SendMessageAsync(
        string sessionId,
        OpenCodePromptRequest request,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}/message", directory);
        ValidateDirectoryScope(url);
        var requestBody = JsonSerializer.Serialize(request, OpenCodeJsonContext.Default.OpenCodePromptRequest);
        LogRequest(_logger, $"POST {url}", null);
        LogRequestBody(_logger, requestBody, null);

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            LogRequestFailed(_logger, (int)response.StatusCode, url, null);
            LogResponseBody(_logger, responseBody, null);
            response.EnsureSuccessStatusCode();
        }

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);

        return DeserializeMessage(doc.RootElement);
    }

    /// <summary>POST /session/{sessionId}/prompt_async?directory={directory} — fire and forget (204).</summary>
    public async Task SendPromptAsyncFireAndForget(
        string sessionId,
        OpenCodePromptRequest request,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}/prompt_async", directory);
        ValidateDirectoryScope(url);
        LogRequest(_logger, $"POST {url}", null);

        var content = new StringContent(
            JsonSerializer.Serialize(request, OpenCodeJsonContext.Default.OpenCodePromptRequest),
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
        ValidateDirectoryScope(url);
        var logUrl = RedactDirectory(url);
        LogRequest(_logger, $"POST {logUrl}", null);

        var content = new StringContent(
            JsonSerializer.Serialize(request, OpenCodeJsonContext.Default.OpenCodeCommandRequest),
            Encoding.UTF8,
            "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        using var response = await _httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None)
            .ConfigureAwait(false);
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
        => (await GetMessagePageAsync(sessionId, directory, limit, before, ct).ConfigureAwait(false)).Messages;

    /// <summary>
    /// Gets the newest <paramref name="limit"/> messages before <paramref name="before"/>, with OpenCode's
    /// cursor for the page after it. OpenCode pages with its own opaque cursor (the <c>X-Next-Cursor</c>
    /// header); a message id as <c>before</c> is rejected with 400.
    /// </summary>
    public async Task<OpenCodeMessagePage> GetMessagePageAsync(
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
        ValidateDirectoryScope(url);
        LogRequest(_logger, $"GET {url}", null);

        using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);

        if (!response.IsSuccessStatusCode)
        {
            LogRequestFailed(_logger, (int)response.StatusCode, url, null);
            response.EnsureSuccessStatusCode();
        }

        var nextCursor = response.Headers.TryGetValues("X-Next-Cursor", out var cursors)
            ? cursors.FirstOrDefault(cursor => !string.IsNullOrWhiteSpace(cursor))
            : null;

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new OpenCodeMessagePage([], null);

        return new OpenCodeMessagePage(DeserializeMessages(doc.RootElement), nextCursor);
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
            if (DeserializeMessage(element) is { } message)
                result.Add(message);
        }

        return result;
    }

    private static OpenCodeMessageWithParts? DeserializeMessage(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty("info", out var infoEl) || infoEl.ValueKind != JsonValueKind.Object)
            return null;

        // Read role from raw JSON — avoid polymorphic deserialization of the abstract base type,
        // which requires the discriminator to be the first property in STJ polymorphism.
        if (!infoEl.TryGetProperty("role", out var roleEl))
            return null;

        var role = roleEl.GetString();
        if (role is not ("user" or "assistant"))
            return null;

        OpenCodeMessageInfo? info = role == "assistant"
            ? OpenCodeMessageDeserializer.DeserializeAssistantMessage(infoEl)
            : OpenCodeMessageDeserializer.DeserializeUserMessage(infoEl);

        if (info is null)
            return null;

        var parts = element.TryGetProperty("parts", out var partsEl)
            ? DeserializeParts(partsEl)
            : [];

        return new OpenCodeMessageWithParts { Info = info, Parts = parts };
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
            var part = OpenCodePartDeserializer.DeserializePart(partEl);

            if (part is not null)
                result.Add(part);
        }

        return result;
    }

    /// <summary>POST /session/{sessionId}/abort?directory={directory}</summary>
    public async Task<bool> AbortAsync(string sessionId, string directory, CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}/abort", directory);
        ValidateDirectoryScope(url);
        LogRequest(_logger, $"POST {url}", null);

        using var response = await _httpClient.PostAsync(url, null, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);
        return response.IsSuccessStatusCode;
    }

    /// <summary>POST /question/{requestId}/reply?directory={directory}</summary>
    public async Task AnswerQuestionAsync(
        string requestId,
        IReadOnlyList<IReadOnlyList<string>> answers,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/question/{Uri.EscapeDataString(requestId)}/reply", directory);
        var body = new OpenCodeQuestionReplyRequest { Answers = answers };
        await PostVoidAsync(url, body, OpenCodeJsonContext.Default.OpenCodeQuestionReplyRequest, ct).ConfigureAwait(false);
    }

    /// <summary>POST /question/{requestId}/reject?directory={directory}</summary>
    public async Task RejectQuestionAsync(string requestId, string directory, CancellationToken ct)
    {
        var url = BuildUrl($"/question/{Uri.EscapeDataString(requestId)}/reject", directory);
        var body = new OpenCodeQuestionRejectRequest();
        await PostVoidAsync(url, body, OpenCodeJsonContext.Default.OpenCodeQuestionRejectRequest, ct).ConfigureAwait(false);
    }

    /// <summary>POST /permission/{requestId}/reply?directory={directory}</summary>
    public async Task ReplyPermissionAsync(
        string requestId,
        string reply,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/permission/{Uri.EscapeDataString(requestId)}/reply", directory);
        var body = new OpenCodePermissionReplyRequest { Reply = reply };
        await PostVoidAsync(url, body, OpenCodeJsonContext.Default.OpenCodePermissionReplyRequest, ct).ConfigureAwait(false);
    }

    /// <summary>POST /session/{sessionId}/fork?directory={directory}</summary>
    public async Task<OpenCodeSessionInfo> ForkSessionAsync(
        string sessionId,
        OpenCodeForkRequest request,
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl($"/session/{Uri.EscapeDataString(sessionId)}/fork", directory);
        return await PostAsync(url, request, OpenCodeJsonContext.Default.OpenCodeForkRequest, OpenCodeJsonContext.Default.OpenCodeSessionInfo, ct).ConfigureAwait(false);
    }

    /// <summary>GET /agent?directory={directory}</summary>
    public async Task<IReadOnlyList<OpenCodeAgentInfo>> GetAgentsAsync(string directory, CancellationToken ct)
    {
        var url = BuildUrl("/agent", directory);
        return await GetAsync(url, OpenCodeJsonContext.Default.ListOpenCodeAgentInfo, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>GET /command?directory={directory}</summary>
    public async Task<IReadOnlyList<OpenCodeCommandInfo>> GetCommandsAsync(string directory, CancellationToken ct)
    {
        var url = BuildUrl("/command", directory);
        return await GetAsync(url, OpenCodeJsonContext.Default.ListOpenCodeCommandInfo, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>GET /provider?directory={directory}</summary>
    public async Task<OpenCodeProvidersResponse> GetProvidersAsync(string directory, CancellationToken ct)
    {
        var url = BuildUrl("/provider", directory);
        return await GetAsync(url, OpenCodeJsonContext.Default.OpenCodeProvidersResponse, ct).ConfigureAwait(false);
    }

    /// <summary>GET /session/status?directory={directory}</summary>
    public async Task<Dictionary<string, OpenCodeSessionStatus>> GetSessionStatusAsync(
        string directory,
        CancellationToken ct)
    {
        var url = BuildUrl("/session/status", directory);
        return await GetAsync(url, OpenCodeJsonContext.Default.DictionaryStringOpenCodeSessionStatus, ct).ConfigureAwait(false)
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
        await foreach (var evt in SubscribeToEventsAsync(directory, static () => Task.CompletedTask, ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// GET /event?directory={directory} — SSE stream.
    /// Reconnects on disconnect with exponential backoff until cancellation is requested.
    /// </summary>
    public async IAsyncEnumerable<OpenCodeSseEvent> SubscribeToEventsAsync(
        string directory,
        Func<Task> connectedAsync,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connectedAsync);

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
                    ValidateDirectoryScope(url);
                    LogRequest(_logger, $"GET {url} (SSE)", null);

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    using var response = await _httpClient
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);

                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await connectedAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(stream);

                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (line is null) break;

                        if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                        var json = line["data: ".Length..];
                        OpenCodeSseEvent? evt;
                        try
                        {
                            evt = JsonSerializer.Deserialize(json, OpenCodeJsonContext.Default.OpenCodeSseEvent);
                        }
                        catch (JsonException)
                        {
                            continue;
                        }

                        if (evt is null) continue;

                        if (evt.Type is EventTypes.ServerHeartbeat or EventTypes.ServerConnected)
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
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // Shutting down — don't log reconnect attempts.
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

    private async Task<T> GetAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        ValidateDirectoryScope(url);
        LogRequest(_logger, $"GET {url}", null);
        using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);

        if (!response.IsSuccessStatusCode)
        {
            LogRequestFailed(_logger, (int)response.StatusCode, url, null);
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content
            .ReadFromJsonAsync(typeInfo, ct)
            .ConfigureAwait(false);

        return result!;
    }

    private async Task PostVoidAsync<TReq>(string url, TReq body, JsonTypeInfo<TReq> reqTypeInfo, CancellationToken ct)
    {
        ValidateDirectoryScope(url);
        var requestBody = JsonSerializer.Serialize(body, reqTypeInfo);
        LogRequest(_logger, $"POST {url}", null);
        LogRequestBody(_logger, requestBody, null);

        var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            LogRequestFailed(_logger, (int)response.StatusCode, url, null);
            LogResponseBody(_logger, responseBody, null);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task<TResp> PostAsync<TReq, TResp>(string url, TReq? body, JsonTypeInfo<TReq> reqTypeInfo, JsonTypeInfo<TResp> respTypeInfo, CancellationToken ct)
    {
        ValidateDirectoryScope(url);
        LogRequest(_logger, $"POST {url}", null);

        StringContent? content = null;
        if (body is not null)
        {
            var requestBody = JsonSerializer.Serialize(body, reqTypeInfo);
            LogRequestBody(_logger, requestBody, null);
            content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        LogResponse(_logger, (int)response.StatusCode, url, null);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            LogRequestFailed(_logger, (int)response.StatusCode, url, null);
            LogResponseBody(_logger, responseBody, null);
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content
            .ReadFromJsonAsync(respTypeInfo, ct)
            .ConfigureAwait(false);

        return result!;
    }

    private static string BuildUrl(string path, string directory)
    {
        char separator = path.Contains('?') ? '&' : '?';
        return $"{path}{separator}directory={Uri.EscapeDataString(directory)}";
    }

    private void ValidateDirectoryScope(string url)
    {
        if (_expectedDirectory is null)
        {
            return;
        }

        var actualDirectory = GetDirectoryQueryValue(url);
        if (string.Equals(actualDirectory, _expectedDirectory, StringComparison.Ordinal))
        {
            return;
        }

        LogDirectoryScopeRejected(_logger, _expectedDirectory, actualDirectory, null);
        throw new InvalidOperationException(
            "Cannot route OpenCode API request because the request directory does not match the leased session working directory.");
    }

    private static string? GetDirectoryQueryValue(string url)
    {
        var queryStart = url.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0 || queryStart == url.Length - 1)
        {
            return null;
        }

        var query = url.AsSpan(queryStart + 1);
        while (!query.IsEmpty)
        {
            var separatorIndex = query.IndexOf('&');
            var parameter = separatorIndex < 0 ? query : query[..separatorIndex];
            if (parameter.StartsWith("directory=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parameter["directory=".Length..].ToString());
            }

            if (separatorIndex < 0)
            {
                break;
            }

            query = query[(separatorIndex + 1)..];
        }

        return null;
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
