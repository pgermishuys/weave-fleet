using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Pages;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Fleet as the browser behind OpenCode 2's browser plugin (<c>@opencode/plugin-browser</c>, RPC
/// <c>experimental.browser</c>, protocol version 4), which gives agents <c>tools.browser.*</c> in Code Mode.
/// <para>
/// While Settings → Browser is on, every session that attaches to a server is attached as its browser: a pending
/// <c>attach</c> call for the attachment's life, and an allow for the <c>browser</c> action after the deny every Fleet
/// session has (<see cref="OpenCode2HttpClient.DenyBrowser"/>), so the agent sees the tools only while they work. V2
/// announces each request on the server's event stream (<c>rpc.experimental.browser.control</c>, ids only); Fleet fetches
/// it (<c>command</c>), runs it in the agent's own tab (<see cref="IAgentBrowser"/>), reports the tabs (<c>state</c>, which
/// V2 resolves tab ids from) and answers (<c>result</c>). <c>preview</c> shows a page in a page canvas, as
/// <c>fleet_page_show</c> does. Switching the browser off detaches every session of that user and hides the tools again.
/// </para>
/// </summary>
internal sealed partial class OpenCode2BrowserAttachments
{
    internal const string RpcId = "experimental.browser";
    internal const string ControlEvent = "rpc.experimental.browser.control";
    private const int Version = 4;

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<IEnumerable<OpenCode2Server>> _servers;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Attachment> _byConnection = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Attachment> _bySession = new(StringComparer.Ordinal);
    private int _subscribed;

    public OpenCode2BrowserAttachments(IServiceScopeFactory scopes, Func<IEnumerable<OpenCode2Server>> servers, ILogger logger)
    {
        _scopes = scopes;
        _servers = servers;
        _logger = logger;
    }

    /// <summary>A session attached to <paramref name="server"/>: attach Fleet's browser to it when the owner has it on.</summary>
    public void SessionAttached(OpenCode2Server server, string harnessSessionId, OpenCode2SessionContext context)
        => _ = StartAsync(server, harnessSessionId, context);

    /// <summary>A control event from <paramref name="server"/>'s stream.</summary>
    public void Control(OpenCode2Server server, JsonElement data)
    {
        var connection = data.TryGetProperty("connectionID", out var c) ? c.GetString() : null;
        if (connection is null || !_byConnection.TryGetValue(connection, out var attachment))
            return;
        switch (data.TryGetProperty("type", out var t) ? t.GetString() : null)
        {
            case "attached":
                if (data.TryGetProperty("version", out var version) && version.GetInt32() != Version)
                {
                    LogVersion(_logger, attachment.HarnessSessionId, version.GetInt32());
                    attachment.Stop();
                    return;
                }

                _ = attachment.OnAttachedAsync();
                break;
            case "command" when data.TryGetProperty("requestID", out var request) && request.GetString() is { } requestId:
                _ = attachment.OnCommandAsync(requestId);
                break;
            case "cancel" when data.TryGetProperty("requestID", out var cancelled) && cancelled.GetString() is { } cancelledId:
                attachment.Cancel(cancelledId);
                break;
        }
    }

    /// <summary>The sessions attached now, for tests.</summary>
    internal int Count => _bySession.Count;

    private async Task StartAsync(OpenCode2Server server, string harnessSessionId, OpenCode2SessionContext context)
    {
        try
        {
            Subscribe();
            if (!await EnabledAsync(context.OwnerUserId).ConfigureAwait(false))
                return;

            using var attachment = new Attachment(this, server, harnessSessionId, context);
            if (!_bySession.TryAdd(Key(server, harnessSessionId), attachment))
                return;
            _byConnection[attachment.ConnectionId] = attachment;
            try
            {
                await attachment.RunAsync().ConfigureAwait(false);
            }
            finally
            {
                _bySession.TryRemove(new KeyValuePair<string, Attachment>(Key(server, harnessSessionId), attachment));
                _byConnection.TryRemove(attachment.ConnectionId, out _);
                await attachment.HideToolsAsync().ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LogFailed(_logger, harnessSessionId, error);
        }
    }

    private void Subscribe()
    {
        if (Interlocked.Exchange(ref _subscribed, 1) == 1)
            return;
        using var scope = _scopes.CreateScope();
        if (scope.ServiceProvider.GetService<AgentBrowserSettingsChanges>() is { } changes)
            changes.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(string userId, AgentBrowserSettings settings)
    {
        if (!settings.Enabled)
        {
            foreach (var attachment in _bySession.Values.Where(a => a.Context.OwnerUserId == userId))
                attachment.Stop();
            return;
        }

        foreach (var server in _servers().Where(server => server.IsRunning && server.OwnerUserId == userId))
        {
            foreach (var (harnessSessionId, context) in server.AttachedSessions)
                _ = StartAsync(server, harnessSessionId, context);
        }
    }

    private async Task<bool> EnabledAsync(string userId)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>();
        using (users.Begin(userId))
            return (await scope.ServiceProvider.GetRequiredService<AgentBrowserAccess>().SettingsAsync().ConfigureAwait(false)).Enabled;
    }

    private static string Key(OpenCode2Server server, string harnessSessionId)
        => RuntimeHelpers.GetHashCode(server).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + harnessSessionId;

    [LoggerMessage(Level = LogLevel.Information, Message = "Fleet's browser attached to OpenCode 2 session {HarnessSessionId}")]
    private static partial void LogAttached(ILogger logger, string harnessSessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fleet's browser left OpenCode 2 session {HarnessSessionId}: {Reason}")]
    private static partial void LogDetached(ILogger logger, string harnessSessionId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenCode 2's browser plugin on session {HarnessSessionId} speaks protocol {Version}; Fleet speaks 4, so it stays off")]
    private static partial void LogVersion(ILogger logger, string harnessSessionId, int version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fleet's browser for OpenCode 2 session {HarnessSessionId} failed")]
    private static partial void LogFailed(ILogger logger, string harnessSessionId, Exception exception);

    /// <summary>One session's attachment: the pending <c>attach</c> call, and the commands it answers.</summary>
    private sealed class Attachment(OpenCode2BrowserAttachments owner, OpenCode2Server server, string harnessSessionId, OpenCode2SessionContext context)
        : IDisposable
    {
        public void Dispose() => _stop.Dispose();

        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);

        public string ConnectionId { get; } = Guid.NewGuid().ToString();
        public string HarnessSessionId { get; } = harnessSessionId;
        public OpenCode2SessionContext Context { get; } = context;

        public void Stop() => _stop.Cancel();

        public void Cancel(string requestId)
        {
            if (_running.TryGetValue(requestId, out var running))
                running.Cancel();
        }

        /// <summary>Holds the <c>attach</c> call open until V2 ends it (closed, replaced) or Fleet stops it.</summary>
        public async Task RunAsync()
        {
            string reason;
            try
            {
                var ended = await server.Client.BrowserRpcAsync(
                    "attach", Context.WorkingDirectory, Input(new JsonObject { ["version"] = Version }), longLived: true, _stop.Token).ConfigureAwait(false);
                reason = ended?.GetValueKind() == JsonValueKind.String ? ended.GetValue<string>() : "ended";
            }
            catch (OperationCanceledException)
            {
                reason = "switched off";
            }
            catch (HttpRequestException error)
            {
                reason = error.Message;
            }

            LogDetached(owner._logger, HarnessSessionId, reason);
        }

        public async Task OnAttachedAsync()
        {
            try
            {
                LogAttached(owner._logger, HarnessSessionId);
                await SetRulesAsync(attached: true).ConfigureAwait(false);
                await PublishAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                LogFailed(owner._logger, HarnessSessionId, error);
            }
        }

        /// <summary>The browser tools out of sight again, once Fleet isn't the session's browser.</summary>
        public async Task HideToolsAsync()
        {
            if (!server.IsRunning)
                return;
            try
            {
                await SetRulesAsync(attached: false).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                // The session or its server is gone; so are its tools.
            }
        }

        private async Task SetRulesAsync(bool attached)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var info = await server.Client.GetSessionAsync(HarnessSessionId, timeout.Token).ConfigureAwait(false);
            if (info is null)
                return;
            var rules = info.Permissions ?? OpenCode2HttpClient.AllowAll;
            var wanted = OpenCode2HttpClient.WithBrowser(rules, attached);
            if (!wanted.SequenceEqual(rules))
                await server.Client.SetPermissionsAsync(HarnessSessionId, wanted, timeout.Token).ConfigureAwait(false);
        }

        public async Task OnCommandAsync(string requestId)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _running[requestId] = cancel;
            try
            {
                var command = await server.Client.BrowserRpcAsync("command", Context.WorkingDirectory, Input(new JsonObject { ["requestID"] = requestId }), longLived: false, cancel.Token).ConfigureAwait(false);
                if (command?["action"] is not JsonObject action)
                    return;

                var outcome = await RunAsync(action, cancel.Token).ConfigureAwait(false);
                // V2 knows a tab only from the inventory: it has to have it before a result names the tab.
                await PublishAsync(cancel.Token).ConfigureAwait(false);
                await server.Client.BrowserRpcAsync("result", Context.WorkingDirectory, Input(new JsonObject { ["requestID"] = requestId, ["outcome"] = outcome }), longLived: false, cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled by the agent's script, or the attachment ended.
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                LogFailed(owner._logger, HarnessSessionId, error);
            }
            finally
            {
                _running.TryRemove(requestId, out _);
            }
        }

        private async Task<JsonObject> RunAsync(JsonObject action, CancellationToken ct)
        {
            var kind = (string?)action["type"] ?? string.Empty;
            await using var scope = owner._scopes.CreateAsyncScope();
            if (kind == "preview")
                return await PreviewAsync(scope.ServiceProvider, (string?)action["path"], ct).ConfigureAwait(false);

            var browser = scope.ServiceProvider.GetRequiredService<IAgentBrowser>();
            var result = await browser.RunAsync(new AgentBrowserCall(Context.FleetSessionId, Context.OwnerUserId, OpenCode2BrowserJson.Action(kind, action)), ct).ConfigureAwait(false);
            return OpenCode2BrowserJson.Outcome(kind, result);
        }

        /// <summary>The plugin's <c>preview</c>: a page in a page canvas, through the same bridge as <c>fleet_page_show</c>.</summary>
        private async Task<JsonObject> PreviewAsync(IServiceProvider services, string? path, CancellationToken ct)
        {
            if (services.GetService<PageBridge>() is not { } pages)
                return OpenCode2BrowserJson.Failure(AgentBrowserFailures.Unsupported, "Fleet can't show files here. Use fleet_page_show.");
            var shown = await pages.ShowAsync(server.BridgeToken, HarnessSessionId, path, title: null, ct: ct).ConfigureAwait(false);
            return shown.IsSuccess
                ? OpenCode2BrowserJson.Success(new JsonObject { ["path"] = path ?? string.Empty })
                : OpenCode2BrowserJson.Failure(AgentBrowserFailures.Failed, shown.Error.Message + " Fleet shows HTML pages; for other files, tell the user the path.");
        }

        private async Task PublishAsync(CancellationToken ct)
        {
            await using var scope = owner._scopes.CreateAsyncScope();
            var (tabs, focused) = scope.ServiceProvider.GetRequiredService<IAgentBrowser>().TabsOf(Context.FleetSessionId);
            await server.Client.BrowserRpcAsync("state", Context.WorkingDirectory, Input(new JsonObject { ["state"] = OpenCode2BrowserJson.State(tabs, focused) }), longLived: false, ct).ConfigureAwait(false);
        }

        private JsonObject Input(JsonObject fields)
        {
            fields["sessionID"] = HarnessSessionId;
            fields["connectionID"] = ConnectionId;
            return fields;
        }
    }
}

/// <summary>OpenCode 2's browser plugin's JSON (<c>Browser.Action</c>, its operations' outputs, <c>Browser.Outcome</c>) and Fleet's typed actions and results.</summary>
internal static class OpenCode2BrowserJson
{
    private const int Short = 2_048;

    public static AgentBrowserAction Action(string kind, JsonObject a) => new(kind)
    {
        TabId = (string?)a["tabID"],
        Url = (string?)a["url"],
        Focus = (bool?)a["focus"],
        Ref = (string?)a["ref"],
        FromRef = (string?)a["from"],
        ToRef = (string?)a["to"],
        Text = (string?)a["text"],
        Key = (string?)a["key"],
        Script = (string?)a["script"],
        Button = (string?)a["button"],
        Count = (int?)a["count"],
        Modifiers = Strings(a["modifiers"]),
        Values = Strings(a["values"]),
        Checked = (bool?)a["checked"],
        Fields = a["fields"] is JsonArray fields ? [.. fields.OfType<JsonObject>().Select(Field)] : null,
        DeltaX = (int?)a["deltaX"],
        DeltaY = (int?)a["deltaY"],
        Condition = (string?)a["condition"],
        TimeoutMs = (int?)a["timeoutMs"],
        Depth = (int?)a["depth"],
        FullPage = (bool?)a["fullPage"],
        Format = (string?)a["format"],
        Quality = (int?)a["quality"],
        MaxWidth = (int?)a["maxWidth"],
        DialogAction = kind == AgentBrowserKinds.Dialog ? (string?)a["action"] : null,
        PromptText = (string?)a["promptText"],
        Level = (string?)a["level"],
        Limit = (int?)a["limit"],
        UrlContains = (string?)a["urlContains"],
        ResourceType = (string?)a["resourceType"],
        RequestId = kind == AgentBrowserKinds.NetworkGet ? (string?)a["id"] : null,
        IncludeBody = (bool?)a["includeBody"],
        MaxBodyChars = (int?)a["maxBodyChars"],
    };

    private static AgentFormField Field(JsonObject f) => new(
        (string?)f["ref"] ?? string.Empty,
        (string?)f["type"] ?? "text",
        (string?)f["value"],
        Strings(f["values"]),
        (bool?)f["checked"]);

    private static List<string>? Strings(JsonNode? node) => node is JsonArray array ? [.. array.Select(item => (string?)item).OfType<string>()] : null;

    /// <summary><c>Browser.Outcome</c> for <paramref name="result"/>, shaped as operation <paramref name="kind"/>'s output.</summary>
    public static JsonObject Outcome(string kind, AgentBrowserResult result)
    {
        if (result.Failure is { } failure)
            return Failure(failure.Code, failure.Message);

        JsonNode value = kind switch
        {
            AgentBrowserKinds.TabsList or AgentBrowserKinds.TabsClose => State(result.Tabs ?? [], result.FocusedTabId),
            AgentBrowserKinds.Frames => With(result, new JsonObject
            {
                ["frames"] = Array(result.Frames ?? [], frame => Object(new JsonObject
                {
                    ["id"] = frame.Id,
                    ["parentID"] = frame.ParentId,
                    ["url"] = frame.Url,
                    ["name"] = Cut(frame.Name),
                })),
            }),
            AgentBrowserKinds.Snapshot or AgentBrowserKinds.Find => With(result, new JsonObject { ["content"] = result.Content ?? string.Empty, ["truncated"] = result.Truncated }),
            AgentBrowserKinds.Evaluate => With(result, new JsonObject { ["value"] = result.Value is { } v ? JsonNode.Parse(v.GetRawText()) : null }),
            AgentBrowserKinds.Screenshot => With(result, []),
            AgentBrowserKinds.Dialog => With(result, new JsonObject
            {
                ["dialog"] = result.Dialog is { } dialog
                    ? new JsonObject { ["type"] = Cut(dialog.Type), ["message"] = dialog.Message, ["defaultValue"] = Cut(dialog.DefaultValue) }
                    : null,
            }),
            AgentBrowserKinds.Console => With(result, new JsonObject
            {
                ["messages"] = Array(result.Console ?? [], Console),
                ["truncated"] = result.Truncated,
                ["dropped"] = result.Dropped,
            }),
            AgentBrowserKinds.NetworkList => With(result, new JsonObject
            {
                ["requests"] = Array(result.Requests ?? [], Request),
                ["truncated"] = result.Truncated,
                ["dropped"] = result.Dropped,
            }),
            AgentBrowserKinds.NetworkGet when result.Request is { } detail => With(result, new JsonObject
            {
                ["request"] = Request(detail.Request),
                ["requestHeaders"] = Array(detail.RequestHeaders, Header),
                ["responseHeaders"] = Array(detail.ResponseHeaders, Header),
                ["headersTruncated"] = false,
                ["requestBody"] = Body(detail.RequestBody),
                ["responseBody"] = Body(detail.ResponseBody),
            }),
            _ => result.Tab is { } tab ? Tab(tab) : State(result.Tabs ?? [], result.FocusedTabId),
        };

        var files = new JsonArray();
        if (result.Image is { } image)
        {
            files.Add((JsonNode)new JsonObject
            {
                ["id"] = "file_" + Guid.NewGuid().ToString("D"),
                ["name"] = "screenshot." + (result.ImageMime?.Split('/').LastOrDefault() ?? "png"),
                ["mime"] = result.ImageMime ?? "image/png",
                ["data"] = Convert.ToBase64String(image),
            });
        }

        return Success(value, files);
    }

    public static JsonObject Success(JsonNode value, JsonArray? files = null)
        => new() { ["type"] = "success", ["result"] = new JsonObject { ["value"] = value, ["files"] = files ?? [] } };

    public static JsonObject Failure(string code, string message)
        => new() { ["type"] = "failure", ["code"] = code, ["message"] = Cut(message) };

    /// <summary><c>Browser.State</c>: the session's tabs and the focused one.</summary>
    public static JsonObject State(IReadOnlyList<AgentTab> tabs, string? focused)
        => new() { ["tabs"] = Array(tabs, Tab), ["focusedTabID"] = focused };

    public static JsonObject Tab(AgentTab tab) => Object(new JsonObject
    {
        ["id"] = tab.Id,
        ["url"] = tab.Url.Length <= 16_384 ? tab.Url : tab.Url[..16_384],
        ["title"] = Cut(tab.Title),
        ["loading"] = tab.Loading,
        ["loadError"] = tab.LoadError is { } error ? Cut(error) : null,
        ["canGoBack"] = tab.CanGoBack,
        ["canGoForward"] = tab.CanGoForward,
        ["generation"] = tab.Generation,
    });

    private static JsonObject With(AgentBrowserResult result, JsonObject output)
    {
        output["tab"] = result.Tab is { } tab ? Tab(tab) : null;
        return output;
    }

    private static JsonObject Console(AgentConsoleEntry entry)
    {
        var json = new JsonObject
        {
            ["id"] = entry.Id,
            ["timestampMs"] = entry.TimestampMs,
            ["level"] = entry.Level,
            ["text"] = entry.Text,
            ["textTruncated"] = entry.TextTruncated,
        };
        if (entry.SourceUrl is { } url)
            json["source"] = new JsonObject { ["url"] = url, ["line"] = Math.Max(0, entry.Line ?? 0), ["column"] = Math.Max(0, entry.Column ?? 0) };
        return json;
    }

    private static JsonObject Request(AgentNetworkRequest request)
    {
        var state = request.State is "pending" or "completed" or "failed" ? request.State : "completed";
        return Object(new JsonObject
        {
            ["id"] = request.Id,
            ["url"] = request.Url,
            ["method"] = Cut(request.Method),
            ["resourceType"] = request.ResourceType,
            ["timestampMs"] = request.TimestampMs,
            ["statusCode"] = request.StatusCode,
            ["state"] = state,
            ["durationMs"] = state == "pending" ? null : request.DurationMs ?? 0,
            ["failure"] = state == "failed" ? Cut(request.Failure ?? "failed") : null,
        });
    }

    private static JsonObject Header(AgentHeader header) => new() { ["name"] = Cut(header.Name), ["value"] = header.Value };

    private static JsonObject Body(AgentBody body) => body.State switch
    {
        "text" => new JsonObject { ["state"] = "text", ["text"] = body.Text ?? string.Empty, ["truncated"] = body.Truncated },
        "unavailable" => new JsonObject { ["state"] = "unavailable", ["reason"] = body.Reason ?? "notCaptured" },
        _ => new JsonObject { ["state"] = body.State },
    };

    private static JsonArray Array<T>(IEnumerable<T> items, Func<T, JsonObject> map)
    {
        var array = new JsonArray();
        foreach (var item in items)
            array.Add((JsonNode)map(item));
        return array;
    }

    /// <summary>The object without its null fields: the plugin's optional fields are left out, never null.</summary>
    private static JsonObject Object(JsonObject json)
    {
        foreach (var key in json.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList())
            json.Remove(key);
        return json;
    }

    private static string Cut(string text) => text.Length <= Short ? text : text[..Short];
}
