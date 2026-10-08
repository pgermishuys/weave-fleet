using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Memory;
using WeaveFleet.Application.Pages;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Walkthroughs;
using WeaveFleet.Application.Workflows;

namespace WeaveFleet.Application.FleetTools;

/// <summary>What one of Fleet's tools gave back, as an MCP result carries it: the text the model reads, and any images.</summary>
public sealed record FleetToolResult(string Text, IReadOnlyList<CanvasToolAttachment> Images, bool IsError);

/// <summary>
/// Fleet's tools for a harness that takes them over MCP (<see cref="FleetToolCatalog"/>). Each call runs in process on the
/// same bridges as the plugins' calls to <c>/api/bridge/*</c>, with the arguments read the way the plugin reads them, so a
/// tool behaves the same on every harness. A session lists and calls only the tools its switches give it
/// (<see cref="FleetToolSettings"/>).
/// </summary>
public sealed class FleetToolCalls(
    IEnumerable<IHarnessMcpCalls> harnesses,
    IEnumerable<IHarnessCanvasCallerResolver> callers,
    FleetToolSettings settings,
    FleetToolCallRecords records,
    CanvasBridge canvases,
    BrowserBridge browser,
    AgentBrowserBridge agentBrowser,
    PageBridge pages,
    WalkthroughBridge walkthroughs,
    SessionReadBridge sessionRead,
    SessionMessageBridge messages,
    MachineHandoffBridge handoff,
    AgentMemoryBridge memory,
    WorkflowStepBridge steps)
{
    /// <summary>The tools the process with <paramref name="bridgeToken"/> has; null when Fleet can't place it.</summary>
    public async Task<IReadOnlyList<FleetToolDefinition>?> ListAsync(string bridgeToken, CancellationToken ct = default)
    {
        var call = await PlaceAsync(bridgeToken, default, ct).ConfigureAwait(false);
        var caller = call is null ? null : await callers.ResolveAsync(bridgeToken, call.HarnessSessionId, ct).ConfigureAwait(false);
        return caller is null ? null : FleetToolCatalog.For(await settings.ForSessionAsync(caller.UserId, caller.FleetSessionId).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs <paramref name="name"/>. Null when the session has no tool by that name: Fleet has none, or its switch is off.
    /// A call Fleet can't place, or one its tool refuses, comes back as an error the model reads.
    /// </summary>
    /// <param name="meta">The request's <c>params._meta</c>, which says to the harness who made the call.</param>
    public async Task<FleetToolResult?> CallAsync(string bridgeToken, string? name, JsonElement arguments, JsonElement meta, CancellationToken ct = default)
    {
        if (FleetToolCatalog.Find(name) is not { } tool)
            return null;

        var call = await PlaceAsync(bridgeToken, meta, ct).ConfigureAwait(false);
        var caller = call is null ? null : await callers.ResolveAsync(bridgeToken, call.HarnessSessionId, ct).ConfigureAwait(false);
        if (call is null || caller is null)
            return Failed(CanvasBridge.UnknownCallerMessage);
        if (!(await settings.ForSessionAsync(caller.UserId, caller.FleetSessionId).ConfigureAwait(false)).Allows(tool.Requires))
            return null;

        var args = arguments.ValueKind == JsonValueKind.Object ? arguments : default;
        var result = await RunAsync(tool.Name, bridgeToken, call, args, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            return Failed(result.Error.Message);

        var output = result.Value;
        if (call.CallId is { } callId)
            records.Record(callId, new FleetToolCallRecord(output.Title, Metadata(output)));
        return new FleetToolResult(output.Output, output.Attachments ?? [], IsError: false);
    }

    private async Task<HarnessMcpCall?> PlaceAsync(string bridgeToken, JsonElement meta, CancellationToken ct)
    {
        foreach (var harness in harnesses)
        {
            if (await harness.PlaceAsync(bridgeToken, meta, ct).ConfigureAwait(false) is { } call)
                return call;
        }

        return null;
    }

    /// <summary>Calls the tool's bridge as the plugin does: an empty string the plugin turns into null is null here too.</summary>
    private Task<CanvasResult<CanvasToolOutput>> RunAsync(string name, string token, HarnessMcpCall call, JsonElement args, CancellationToken ct)
    {
        var session = call.HarnessSessionId;
        return name switch
        {
            "fleet_canvas_list" => canvases.ListAsync(token, session, ct),
            "fleet_canvas_open" => canvases.OpenAsync(token, session, String(args, "kind"), String(args, "title"), Node(args, "state"), ct),
            "fleet_canvas_read" => canvases.ReadAsync(token, session, String(args, "canvasId"), ct),
            "fleet_canvas_patch" => canvases.PatchAsync(token, session, String(args, "canvasId"), Node(args, "ops"), ct),
            "fleet_canvas_focus" => canvases.FocusAsync(token, session, String(args, "canvasId"), ct),
            "fleet_page_show" => pages.ShowAsync(token, session, String(args, "path"), String(args, "title"), ct),
            "fleet_walkthrough_show" => walkthroughs.ShowAsync(token, session, String(args, "title"), Element(args, "guide"), ct),
            "fleet_app_start" => browser.AppStartAsync(token, session, String(args, "command"), String(args, "title"), ct),
            "fleet_browser_open" => browser.BrowserOpenAsync(token, session, String(args, "url"), String(args, "title"), ct),
            "fleet_browser_screenshot" => browser.ScreenshotAsync(token, session, String(args, "canvasId"), String(args, "path"), String(args, "viewport"), ct),
            "fleet_browser_read" => agentBrowser.ReadAsync(token, new AgentBrowserToolRequest(
                session, What: String(args, "what"), Text: NonEmpty(args, "text"), CallId: call.CallId), ct),
            "fleet_browser_act" => agentBrowser.ActAsync(token, new AgentBrowserToolRequest(
                session,
                Action: String(args, "action"),
                Ref: NonEmpty(args, "ref"),
                Text: NonEmpty(args, "text"),
                Url: NonEmpty(args, "url"),
                Read: Boolean(args, "read"),
                CallId: call.CallId), ct),
            "fleet_session_read" => sessionRead.ReadAsync(token, session, String(args, "sessionId"), NonEmpty(args, "before"), Limit(args), NonEmpty(args, "machine"), ct),
            "fleet_message" => messages.SendAsync(
                token, session, String(args, "sessionId"), String(args, "text"), Boolean(args, "notifyWhenDone"), NonEmpty(args, "machine"), ct),
            "fleet_machine_list" => handoff.ListAsync(token, session, ct),
            "fleet_session_start" => handoff.StartAsync(
                token,
                session,
                String(args, "machine"),
                String(args, "folder"),
                String(args, "title"),
                String(args, "task"),
                NonEmpty(args, "branch"),
                NonEmpty(args, "harness"),
                Boolean(args, "notifyWhenDone"),
                ct),
            "fleet_memory_save" => memory.SaveAsync(token, session, String(args, "list"), String(args, "text"), String(args, "kind"), String(args, "replaces"), ct),
            "fleet_memory_forget" => memory.ForgetAsync(token, session, String(args, "id"), ct),
            "fleet_step_done" => steps.DoneAsync(token, session, String(args, "outcome"), String(args, "summary"), ct),
            _ => Task.FromResult(CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Invalid, $"Fleet has no tool {name}.")),
        };
    }

    private static FleetToolResult Failed(string message) => new(message, [], IsError: true);

    private static string? String(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                _ => null,
            }
            : null;

    private static string? NonEmpty(JsonElement args, string name) => String(args, name) is { Length: > 0 } text ? text : null;

    /// <summary>A true argument, which models sometimes send as the string "true".</summary>
    private static bool Boolean(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
           && (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && value.GetString() == "true"));

    /// <summary>The page size, from a number or a number sent as a string; none (Fleet's default) for anything else or 0.</summary>
    private static int? Limit(JsonElement args)
        => double.TryParse(String(args, "limit"), NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) && limit is >= 1 and <= int.MaxValue
            ? (int)limit
            : null;

    /// <summary>An object or list argument as the bridge takes it; a model that sent it as JSON text gets it read.</summary>
    private static JsonElement Element(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) ? value : default;

    private static JsonNode? Node(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            try
            {
                return JsonNode.Parse(value.GetString()!);
            }
            catch (JsonException)
            {
                return JsonValue.Create(value.GetString());
            }
        }

        return JsonNode.Parse(value.GetRawText());
    }

    /// <summary>The metadata the OpenCode plugins keep with the call: <c>{canvasId, version, screenshot?}</c>.</summary>
    private static JsonElement Metadata(CanvasToolOutput output)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            if (output.CanvasId is { } canvasId)
                json.WriteString("canvasId", canvasId);
            else
                json.WriteNull("canvasId");
            if (output.Version is { } version)
                json.WriteNumber("version", version);
            else
                json.WriteNull("version");
            if (output.Screenshot is { } shot)
            {
                json.WriteStartObject("screenshot");
                json.WriteString("sessionId", shot.SessionId);
                json.WriteString("id", shot.Id);
                json.WriteNumber("width", shot.Width);
                json.WriteNumber("height", shot.Height);
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
