using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Browser;
using WeaveFleet.Infrastructure.Browser;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// SPIKE (branch spike/opencode2-browser-plugin): Fleet as the "desktop" for OpenCode 2's browser plugin
/// (<c>@opencode/plugin-browser</c>, RPC id <c>experimental.browser</c>, protocol version 4). Not for main.
/// <para>
/// The wire: Fleet reads V2's event stream (<c>GET /api/event</c>), and once it says <c>server.connected</c> posts
/// <c>POST /api/rpc/experimental.browser/attach</c>, which stays pending for the attachment's life. V2 then emits
/// <c>rpc.experimental.browser.control</c> events: <c>attached</c> (ready), <c>command</c> (a request id only) and
/// <c>cancel</c>. For a command Fleet fetches the action with <c>command</c>, runs it in its headless Chrome over CDP,
/// and answers with <c>result</c>. A tab must be in V2's inventory (<c>state</c>) before a result names it.
/// </para>
/// </summary>
public sealed partial class OpenCode2BrowserSpike
{
    private readonly OpenCode2HarnessRuntime _runtime;
    private readonly IScreenshotter _screenshotter;
    private readonly ILogger<OpenCode2BrowserSpike> _logger;
    private readonly ConcurrentDictionary<string, Attachment> _attached = new(StringComparer.Ordinal);

    internal OpenCode2BrowserSpike(OpenCode2HarnessRuntime runtime, IScreenshotter screenshotter, ILogger<OpenCode2BrowserSpike> logger)
    {
        _runtime = runtime;
        _screenshotter = screenshotter;
        _logger = logger;
    }

    /// <summary>The log of every command the agent's browser ran for <paramref name="fleetSessionId"/>.</summary>
    public IReadOnlyList<string> Log(string fleetSessionId)
        => _attached.TryGetValue(fleetSessionId, out var attachment) ? [.. attachment.Log] : [];

    /// <summary>Attaches Fleet's headless Chrome to the session's V2 server; returns what happened in words.</summary>
    public async Task<string> AttachAsync(string ownerUserId, string fleetSessionId, CancellationToken ct)
    {
        if (_runtime.Servers.FindServing(ownerUserId, fleetSessionId) is not { } server)
            return "The session has no running OpenCode 2 server. Send it a prompt first.";
        if (server.SessionFor(fleetSessionId) is not { } session)
            return "The server doesn't list this session.";
        if (_screenshotter is not HeadlessChromeScreenshotter chrome)
            return "Screenshots aren't Fleet's headless Chrome here.";

        var (cdp, problem) = await chrome.SharedBrowserAsync(ct);
        if (cdp is null)
            return problem ?? "No browser.";

        if (_attached.TryRemove(fleetSessionId, out var old))
            await old.DisposeAsync();
        var attachment = new Attachment(server.Client, session.HarnessSessionId, session.Directory, cdp, _logger);
        _attached[fleetSessionId] = attachment;
        attachment.Start();
        var ready = await Task.WhenAny(attachment.Ready.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
        return ready == attachment.Ready.Task
            ? $"Attached to {session.HarnessSessionId} as {attachment.ConnectionId}."
            : "V2 didn't confirm the attachment within 15 s.";
    }

    private static partial class Log2
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Browser spike: {Line}")]
        public static partial void Line(ILogger logger, string line);
    }

    private sealed class Tab(string id, string target, string session)
    {
        public string Id { get; } = id;
        public string Target { get; } = target;
        public string Session { get; } = session;
        public int Generation { get; set; }
        public Dictionary<string, long> Refs { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Attachment(OpenCode2HttpClient client, string sessionId, string directory, CdpConnection cdp, ILogger logger)
        : IAsyncDisposable
    {
        // Wraps console methods on every new document, so `console` can read them back without a CDP event stream.
        private const string ConsoleRecorder =
            "(() => { const k = '__fleetConsole'; window[k] = []; for (const level of ['log','info','warn','error','debug']) { " +
            "const original = console[level].bind(console); console[level] = (...args) => { window[k].push({ level: level === 'warn' ? 'warning' : level === 'log' ? 'info' : level, " +
            "text: args.map(a => typeof a === 'string' ? a : (() => { try { return JSON.stringify(a) } catch { return String(a) } })()).join(' ').slice(0, 2000), timestampMs: Date.now() }); original(...args) } } " +
            "addEventListener('error', e => window[k].push({ level: 'error', text: String(e.message), timestampMs: Date.now() })) })()";

        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, Tab> _tabs = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _run = new(1, 1);
        private string? _focused;

        public string ConnectionId { get; } = Guid.NewGuid().ToString();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Log { get; } = new();

        public void Start() => _ = Task.Run(() => ReadEventsAsync(_stop.Token));

        private void Note(string line)
        {
            var stamped = $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {line}";
            Log.Enqueue(stamped);
            Log2.Line(logger, stamped);
        }

        private async Task<JsonNode?> RpcAsync(string method, JsonObject input, CancellationToken ct, bool longLived = false)
        {
            input["sessionID"] = sessionId;
            input["connectionID"] = ConnectionId;
            var body = new JsonObject { ["input"] = input }.ToJsonString();
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var http = longLived ? client.LongLived : client.Requests;
            using var response = await http.PostAsync(
                $"api/rpc/experimental.browser/{method}?{OpenCode2HttpClient.LocationQuery(directory)}", content, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{method}: {(int)response.StatusCode} {text[..Math.Min(400, text.Length)]}");
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var node = JsonNode.Parse(text);
            if (method == "command")
                Note("command reply " + text[..Math.Min(200, text.Length)]);
            foreach (var key in (string[])["data", "output"])
                if (node is JsonObject wrapped && wrapped.TryGetPropertyValue(key, out var inner))
                    return inner;
            return node;
        }

        private async Task ReadEventsAsync(CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/event");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                using var response = await client.LongLived.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                var data = new StringBuilder();
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        data.Append(line.AsSpan(5).Trim());
                        continue;
                    }
                    if (line.Length != 0 || data.Length == 0)
                        continue;
                    var json = JsonNode.Parse(data.ToString());
                    data.Clear();
                    await OnEventAsync(json, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Note("event stream ended: " + ex.Message);
            }
        }

        private Task OnEventAsync(JsonNode? json, CancellationToken ct)
        {
            var type = json?["type"]?.GetValue<string>();
            if (type == "server.connected")
            {
                Note($"event stream connected; attach {sessionId} as {ConnectionId}");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ended = await RpcAsync("attach", new JsonObject { ["version"] = 4 }, ct, longLived: true);
                        Note("attach returned " + ended?.ToJsonString());
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Note("attach failed: " + ex.Message);
                    }
                }, ct);
                return Task.CompletedTask;
            }
            if (type != "rpc.experimental.browser.control" || json?["data"]?["connectionID"]?.GetValue<string>() != ConnectionId)
                return Task.CompletedTask;

            var control = json["data"]!;
            Note("control " + control.ToJsonString());
            switch (control["type"]?.GetValue<string>())
            {
                case "attached":
                    _ = Task.Run(async () =>
                    {
                        await PublishAsync(ct);
                        Ready.TrySetResult();
                    }, ct);
                    break;
                case "command":
                    var requestId = control["requestID"]!.GetValue<string>();
                    _ = Task.Run(() => HandleAsync(requestId, ct), ct);
                    break;
            }
            return Task.CompletedTask;
        }

        private async Task HandleAsync(string requestId, CancellationToken ct)
        {
            JsonObject outcome;
            string? kind = null;
            try
            {
                var command = await RpcAsync("command", new JsonObject { ["requestID"] = requestId }, ct);
                var action = command!["action"]!.AsObject();
                kind = action["type"]!.GetValue<string>();
                Note("command " + action.ToJsonString()[..Math.Min(300, action.ToJsonString().Length)]);
                // One at a time: a click must not race the snapshot that gave it its ref.
                await _run.WaitAsync(ct);
                try
                {
                    var (value, files) = await ExecuteAsync(kind, action, ct);
                    outcome = new JsonObject
                    {
                        ["type"] = "success",
                        ["result"] = new JsonObject { ["value"] = value, ["files"] = files ?? [] },
                    };
                }
                finally
                {
                    _run.Release();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = new JsonObject
                {
                    ["type"] = "failure",
                    ["code"] = ex is NotSupportedException ? "unsupported" : "failed",
                    ["message"] = ex.Message[..Math.Min(2000, ex.Message.Length)],
                };
            }
            await RpcAsync("result", new JsonObject { ["requestID"] = requestId, ["outcome"] = outcome }, ct);
            Note($"result {kind} {outcome["type"]} " + (outcome["type"]!.GetValue<string>() == "success"
                ? $"{outcome["result"]!["value"]?.ToJsonString().Length} chars, {outcome["result"]!["files"]!.AsArray().Count} file(s)"
                : outcome["message"]!.GetValue<string>()));
        }

        private async Task<JsonObject> InfoAsync(Tab tab, CancellationToken ct)
        {
            using var page = await SendAsync("Runtime.evaluate", w =>
            {
                w.WriteString("expression", "JSON.stringify({u: location.href, t: document.title, l: document.readyState !== 'complete'})");
                w.WriteBoolean("returnByValue", true);
            }, tab.Session, ct);
            var v = JsonNode.Parse(page.RootElement.GetProperty("result").GetProperty("result").GetProperty("value").GetString()!)!;
            using var history = await SendAsync("Page.getNavigationHistory", null, tab.Session, ct);
            var h = history.RootElement.GetProperty("result");
            var current = h.GetProperty("currentIndex").GetInt32();
            var count = h.GetProperty("entries").GetArrayLength();
            var title = v["t"]!.GetValue<string>();
            return new JsonObject
            {
                ["id"] = tab.Id,
                ["url"] = v["u"]!.GetValue<string>(),
                ["title"] = title[..Math.Min(2048, title.Length)],
                ["loading"] = v["l"]!.GetValue<bool>(),
                ["canGoBack"] = current > 0,
                ["canGoForward"] = current < count - 1,
                ["generation"] = tab.Generation,
            };
        }

        private async Task<JsonObject> StateAsync(CancellationToken ct)
        {
            var tabs = new JsonArray();
            foreach (var tab in _tabs.Values)
                tabs.Add((JsonNode)await InfoAsync(tab, ct));
            return new JsonObject { ["tabs"] = tabs, ["focusedTabID"] = _focused };
        }

        private async Task PublishAsync(CancellationToken ct)
            => await RpcAsync("state", new JsonObject { ["state"] = await StateAsync(ct) }, ct);

        private Task<JsonDocument> SendAsync(string method, Action<Utf8JsonWriter>? write, string? session, CancellationToken ct)
            => cdp.SendAsync(method, write, session, ct);

        private async Task NavigateAsync(Tab tab, string url, CancellationToken ct)
        {
            using var loaded = cdp.Expect("Page.loadEventFired", tab.Session);
            (await SendAsync("Page.navigate", w => w.WriteString("url", url), tab.Session, ct)).Dispose();
            await loaded.ArrivedAsync(TimeSpan.FromSeconds(10), ct);
            tab.Generation++;
            tab.Refs.Clear();
        }

        private async Task<(double X, double Y, long Node)> CenterAsync(Tab tab, string reference, CancellationToken ct)
        {
            if (!tab.Refs.TryGetValue(reference.TrimStart('@'), out var node))
                throw new InvalidOperationException("Unknown ref. Take a fresh browser.snapshot and use a returned ref.");
            (await SendAsync("DOM.scrollIntoViewIfNeeded", w => w.WriteNumber("backendNodeId", node), tab.Session, ct)).Dispose();
            using var box = await SendAsync("DOM.getBoxModel", w => w.WriteNumber("backendNodeId", node), tab.Session, ct);
            var q = box.RootElement.GetProperty("result").GetProperty("model").GetProperty("content");
            return ((q[0].GetDouble() + q[4].GetDouble()) / 2, (q[1].GetDouble() + q[5].GetDouble()) / 2, node);
        }

        private async Task<string> SnapshotAsync(Tab tab, CancellationToken ct)
        {
            using var tree = await SendAsync("Accessibility.getFullAXTree", w => w.WriteNumber("depth", 12), tab.Session, ct);
            var nodes = tree.RootElement.GetProperty("result").GetProperty("nodes").EnumerateArray()
                .ToDictionary(n => n.GetProperty("nodeId").GetString()!, n => n.Clone(), StringComparer.Ordinal);
            tab.Refs.Clear();
            var lines = new List<string>();
            var next = 0;
            void Walk(JsonElement node, int level)
            {
                var role = node.TryGetProperty("role", out var r) && r.TryGetProperty("value", out var rv) ? rv.ToString() : "node";
                var ignored = node.TryGetProperty("ignored", out var ig) && ig.GetBoolean();
                if (!ignored)
                {
                    var focusable = node.TryGetProperty("properties", out var props) && props.EnumerateArray()
                        .Any(p => p.GetProperty("name").GetString() == "focusable" && p.GetProperty("value").TryGetProperty("value", out var f) && f.ValueKind == JsonValueKind.True);
                    var actionable = role != "RootWebArea" && (focusable || role is "button" or "link" or "textbox" or "combobox" or "checkbox" or "radio" or "option");
                    var reference = "";
                    if (actionable && node.TryGetProperty("backendDOMNodeId", out var backend))
                    {
                        reference = $"e{++next}";
                        tab.Refs[reference] = backend.GetInt64();
                    }
                    var name = node.TryGetProperty("name", out var nm) && nm.TryGetProperty("value", out var nv) ? nv.ToString() : "";
                    name = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    lines.Add($"{new string(' ', level * 2)}{(reference.Length > 0 ? "@" + reference + " " : "")}[{role}] {JsonSerializer.Serialize(name[..Math.Min(300, name.Length)], FleetSpikeJson.Default.String)}");
                }
                if (node.TryGetProperty("childIds", out var children))
                    foreach (var child in children.EnumerateArray())
                        if (nodes.TryGetValue(child.GetString()!, out var c))
                            Walk(c, level + 1);
            }
            Walk(nodes.Values.First(), 0);
            var text = string.Join('\n', lines);
            return text[..Math.Min(100_000, text.Length)];
        }

        private async Task<(JsonNode? Value, JsonArray? Files)> ExecuteAsync(string kind, JsonObject action, CancellationToken ct)
        {
            Tab? tab = null;
            if (action["tabID"]?.GetValue<string>() is { } tabId && !_tabs.TryGetValue(tabId, out tab))
                throw new InvalidOperationException("This tab is closed. Call browser.tabs.list({}).");

            switch (kind)
            {
                case "tabs.list":
                    return (await StateAsync(ct), null);
                case "tabs.open":
                {
                    string target;
                    using (var created = await SendAsync("Target.createTarget", w => w.WriteString("url", "about:blank"), null, ct))
                        target = created.RootElement.GetProperty("result").GetProperty("targetId").GetString()!;
                    string session;
                    using (var attached = await SendAsync("Target.attachToTarget", w => { w.WriteString("targetId", target); w.WriteBoolean("flatten", true); }, null, ct))
                        session = attached.RootElement.GetProperty("result").GetProperty("sessionId").GetString()!;
                    var opened = new Tab("tab_" + Guid.NewGuid().ToString("D"), target, session);
                    foreach (var method in (string[])["Page.enable", "Runtime.enable", "DOM.enable"])
                        (await SendAsync(method, null, session, ct)).Dispose();
                    (await SendAsync("Page.addScriptToEvaluateOnNewDocument", w => w.WriteString("source", ConsoleRecorder), session, ct)).Dispose();
                    (await SendAsync("Emulation.setDeviceMetricsOverride", w =>
                    {
                        w.WriteNumber("width", 1280);
                        w.WriteNumber("height", 800);
                        w.WriteNumber("deviceScaleFactor", 1);
                        w.WriteBoolean("mobile", false);
                    }, session, ct)).Dispose();
                    _tabs[opened.Id] = opened;
                    if (action["url"]?.GetValue<string>() is { Length: > 0 } url)
                        await NavigateAsync(opened, url, ct);
                    if (action["focus"]?.GetValue<bool>() != false)
                        _focused = opened.Id;
                    await PublishAsync(ct); // V2 resolves tab ids from its copy of the inventory
                    return (await InfoAsync(opened, ct), null);
                }
                case "tabs.focus":
                    _focused = tab!.Id;
                    await PublishAsync(ct);
                    return (await InfoAsync(tab, ct), null);
                case "tabs.close":
                    (await SendAsync("Target.closeTarget", w => w.WriteString("targetId", tab!.Target), null, ct)).Dispose();
                    _tabs.TryRemove(tab!.Id, out _);
                    if (_focused == tab.Id)
                        _focused = null;
                    await PublishAsync(ct);
                    return (await StateAsync(ct), null);
                case "navigate":
                    await NavigateAsync(tab!, action["url"]!.GetValue<string>(), ct);
                    await PublishAsync(ct);
                    return (await InfoAsync(tab!, ct), null);
                case "snapshot":
                    return (new JsonObject { ["tab"] = await InfoAsync(tab!, ct), ["content"] = await SnapshotAsync(tab!, ct), ["truncated"] = false }, null);
                case "click":
                {
                    var (x, y, _) = await CenterAsync(tab!, action["ref"]!.GetValue<string>(), ct);
                    foreach (var type in (string[])["mousePressed", "mouseReleased"])
                        (await SendAsync("Input.dispatchMouseEvent", w =>
                        {
                            w.WriteString("type", type);
                            w.WriteNumber("x", x);
                            w.WriteNumber("y", y);
                            w.WriteString("button", "left");
                            w.WriteNumber("clickCount", 1);
                        }, tab!.Session, ct)).Dispose();
                    await Task.Delay(150, ct);
                    return (await InfoAsync(tab!, ct), null);
                }
                case "fill":
                {
                    var (_, _, node) = await CenterAsync(tab!, action["ref"]!.GetValue<string>(), ct);
                    (await SendAsync("DOM.focus", w => w.WriteNumber("backendNodeId", node), tab!.Session, ct)).Dispose();
                    (await SendAsync("Runtime.evaluate", w => w.WriteString("expression", "document.activeElement.select && document.activeElement.select()"), tab.Session, ct)).Dispose();
                    (await SendAsync("Input.insertText", w => w.WriteString("text", action["text"]!.GetValue<string>()), tab.Session, ct)).Dispose();
                    return (await InfoAsync(tab, ct), null);
                }
                case "evaluate":
                {
                    using var result = await SendAsync("Runtime.evaluate", w =>
                    {
                        w.WriteString("expression", action["script"]!.GetValue<string>());
                        w.WriteBoolean("awaitPromise", true);
                        w.WriteBoolean("returnByValue", true);
                        w.WriteBoolean("userGesture", true);
                    }, tab!.Session, ct);
                    var r = result.RootElement.GetProperty("result");
                    if (r.TryGetProperty("exceptionDetails", out var thrown))
                        throw new InvalidOperationException("Page JavaScript threw: " + thrown.GetProperty("text").GetString());
                    var value = r.GetProperty("result").TryGetProperty("value", out var v) ? JsonNode.Parse(v.GetRawText()) : null;
                    return (new JsonObject { ["tab"] = await InfoAsync(tab, ct), ["value"] = value }, null);
                }
                case "console":
                {
                    using var result = await SendAsync("Runtime.evaluate", w =>
                    {
                        w.WriteString("expression", "JSON.stringify((window.__fleetConsole || []).slice(-100))");
                        w.WriteBoolean("returnByValue", true);
                    }, tab!.Session, ct);
                    var entries = JsonNode.Parse(result.RootElement.GetProperty("result").GetProperty("result").GetProperty("value").GetString()!)!.AsArray();
                    var messages = new JsonArray();
                    var i = 0;
                    foreach (var entry in entries)
                        messages.Add((JsonNode)new JsonObject
                        {
                            ["id"] = (++i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["timestampMs"] = entry!["timestampMs"]!.GetValue<double>(),
                            ["level"] = entry["level"]!.GetValue<string>() switch { "debug" => "debug", "warning" => "warning", "error" => "error", _ => "info" },
                            ["text"] = entry["text"]!.GetValue<string>(),
                            ["textTruncated"] = false,
                        });
                    return (new JsonObject { ["tab"] = await InfoAsync(tab, ct), ["messages"] = messages, ["truncated"] = false, ["dropped"] = 0 }, null);
                }
                case "screenshot":
                {
                    using var shot = await SendAsync("Page.captureScreenshot", w => w.WriteString("format", "png"), tab!.Session, ct);
                    var data = shot.RootElement.GetProperty("result").GetProperty("data").GetString()!;
                    var files = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "file_" + Guid.NewGuid().ToString("D"),
                            ["name"] = "screenshot.png",
                            ["mime"] = "image/png",
                            ["data"] = data,
                        });
                    return (new JsonObject { ["tab"] = await InfoAsync(tab, ct) }, files);
                }
                default:
                    throw new NotSupportedException($"Fleet's spike attachment doesn't run browser.{kind}.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            foreach (var tab in _tabs.Values)
            {
                try
                {
                    (await cdp.SendAsync("Target.closeTarget", w => w.WriteString("targetId", tab.Target))).Dispose();
                }
                catch (CdpException)
                {
                }
            }
            _stop.Dispose();
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class FleetSpikeJson : System.Text.Json.Serialization.JsonSerializerContext;
