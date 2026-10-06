using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Application.Diagnostics;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Fleet's own tools as an MCP server (Streamable HTTP), for harnesses that take tools over MCP (Claude Code): the same
/// tools the OpenCode plugins add (<see cref="FleetToolCatalog"/>), run in process (<see cref="FleetToolCalls"/>).
/// <para>
/// It answers only at <c>{FLEET_URL}/mcp</c>, under a harness process's <c>/agent/{token}</c> prefix: the token is the
/// caller's credential and says which session is calling, as it does for the process's calls to Fleet's API. Anything
/// else gets a 404. It speaks JSON-RPC over plain POSTs and answers each with JSON: <c>initialize</c>, <c>ping</c>,
/// <c>tools/list</c> and <c>tools/call</c>. It sends nothing of its own, so there's no event stream (GET) and no session
/// to end (DELETE).
/// </para>
/// </summary>
public static class McpEndpoints
{
    public const string Path = "/mcp";

    /// <summary>The newest MCP version Fleet's server speaks, for a client that asks for one Fleet doesn't know.</summary>
    public const string LatestProtocolVersion = "2025-11-25";

    /// <summary>The versions Fleet's server can answer in: nothing it uses changed between them.</summary>
    private static readonly string[] ProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18", LatestProtocolVersion];

    // JSON-RPC's error codes.
    internal const int ParseError = -32700;
    internal const int InvalidRequest = -32600;
    internal const int MethodNotFound = -32601;
    internal const int InvalidParams = -32602;

    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, HandleAsync)
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithName("FleetMcp");

        app.MapMethods(Path, [HttpMethods.Get, HttpMethods.Delete], (HttpContext http)
                => http.IsAgentRequest() ? Results.StatusCode(StatusCodes.Status405MethodNotAllowed) : Results.NotFound())
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithName("FleetMcpNoStream");

        return app;
    }

    private static async Task HandleAsync(HttpContext http, FleetToolCalls tools)
    {
        if (http.AgentBridgeToken() is not { } token)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var ct = http.RequestAborted;
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            await WriteAsync(http, Error(null, ParseError, "The request isn't JSON."));
            return;
        }

        using (document)
        {
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object
                || !request.TryGetProperty("method", out var methodValue)
                || methodValue.ValueKind != JsonValueKind.String)
            {
                await WriteAsync(http, Error(IdOf(request), InvalidRequest, "Send one JSON-RPC request, with a method."));
                return;
            }

            // A notification (notifications/initialized, notifications/cancelled) or a client's answer: nothing to say back.
            if (IdOf(request) is not { } id)
            {
                http.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            var parameters = request.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            var response = methodValue.GetString() switch
            {
                "initialize" => Initialize(id, parameters),
                "ping" => Json(new McpResponse<McpEmptyResult>(id, new McpEmptyResult()), McpJsonContext.Default.McpResponseMcpEmptyResult),
                "tools/list" => await ListAsync(id, tools, token, ct),
                "tools/call" => await CallAsync(id, tools, token, parameters, ct),
                var method => Error(id, MethodNotFound, $"Fleet's MCP server has no method {method}."),
            };
            await WriteAsync(http, response);
        }
    }

    private static string Initialize(JsonElement id, JsonElement parameters)
    {
        var asked = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var version)
                    && version.ValueKind == JsonValueKind.String
            ? version.GetString()
            : null;
        var result = new McpInitializeResult(
            ProtocolVersions.Contains(asked) ? asked! : LatestProtocolVersion,
            new McpServerCapabilities(new McpToolsCapability(ListChanged: false)),
            new McpServerInfo("fleet", "Fleet", FleetInstrumentation.ServiceVersion.Split('+')[0]));
        return Json(new McpResponse<McpInitializeResult>(id, result), McpJsonContext.Default.McpResponseMcpInitializeResult);
    }

    private static async Task<string> ListAsync(JsonElement id, FleetToolCalls tools, string token, CancellationToken ct)
    {
        // A process Fleet can't place any more (its session went) has no tools.
        var listed = await tools.ListAsync(token, ct) ?? [];
        var result = new McpToolsListResult([.. listed.Select(tool => new McpTool(tool.Name, tool.Description, tool.InputSchema))]);
        return Json(new McpResponse<McpToolsListResult>(id, result), McpJsonContext.Default.McpResponseMcpToolsListResult);
    }

    private static async Task<string> CallAsync(JsonElement id, FleetToolCalls tools, string token, JsonElement parameters, CancellationToken ct)
    {
        var name = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()
            : null;
        var arguments = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var a) ? a : default;
        var meta = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("_meta", out var m) ? m : default;

        if (await tools.CallAsync(token, name, arguments, meta, ct) is not { } called)
            return Error(id, InvalidParams, $"Unknown tool: {name}");

        // A refusal is the tool's answer, which the model reads (isError), not a failure of the request.
        List<McpContent> content = [new McpContent("text", Text: called.Text)];
        content.AddRange(called.Images.Select(image => new McpContent("image", Data: Convert.ToBase64String(image.Content), MimeType: image.Mime)));
        return Json(new McpResponse<McpToolCallResult>(id, new McpToolCallResult(content, called.IsError)), McpJsonContext.Default.McpResponseMcpToolCallResult);
    }

    /// <summary>The request's id (a string or a number), or null for a notification.</summary>
    private static JsonElement? IdOf(JsonElement request)
        => request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? id.Clone()
            : null;

    private static string Error(JsonElement? id, int code, string message)
        => Json(new McpErrorResponse(id, new McpError(code, message)), McpJsonContext.Default.McpErrorResponse);

    private static string Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => JsonSerializer.Serialize(value, type);

    private static Task WriteAsync(HttpContext http, string json)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(json, http.RequestAborted);
    }
}

#pragma warning restore IL2026

internal sealed record McpResponse<TResult>(JsonElement Id, TResult Result, string Jsonrpc = "2.0");

internal sealed record McpErrorResponse(JsonElement? Id, McpError Error, string Jsonrpc = "2.0");

internal sealed record McpError(int Code, string Message);

internal sealed record McpEmptyResult;

internal sealed record McpInitializeResult(string ProtocolVersion, McpServerCapabilities Capabilities, McpServerInfo ServerInfo);

internal sealed record McpServerCapabilities(McpToolsCapability Tools);

internal sealed record McpToolsCapability(bool ListChanged);

internal sealed record McpServerInfo(string Name, string Title, string Version);

internal sealed record McpToolsListResult(IReadOnlyList<McpTool> Tools);

internal sealed record McpTool(string Name, string Description, JsonElement InputSchema);

internal sealed record McpToolCallResult(IReadOnlyList<McpContent> Content, bool IsError);

/// <summary>A piece of a tool's result: <c>text</c> with its text, or an <c>image</c> as base64 with its type.</summary>
internal sealed record McpContent(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Data = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MimeType = null);

/// <summary>
/// Fleet's MCP messages. An error's id stays even when it's null (a request Fleet couldn't read), as JSON-RPC asks; the
/// content's unused fields are left out.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McpResponse<McpEmptyResult>))]
[JsonSerializable(typeof(McpResponse<McpInitializeResult>))]
[JsonSerializable(typeof(McpResponse<McpToolsListResult>))]
[JsonSerializable(typeof(McpResponse<McpToolCallResult>))]
[JsonSerializable(typeof(McpErrorResponse))]
internal sealed partial class McpJsonContext : JsonSerializerContext
{
}
