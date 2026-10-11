using System.Buffers;
using System.Text.Json;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Mods;

/// <summary>A browser's ask for a tool row's tree (<c>docs/mods/api.md</c>, "Render sites").</summary>
/// <param name="Site"><c>ToolUse</c> or <c>ToolResult</c>.</param>
/// <param name="RequestId">The tool call's id.</param>
/// <param name="Props">The tool row props, as the browser has them.</param>
public sealed record ModRenderRequest(string? Site, string? RequestId, JsonElement? Props);

/// <summary>A mod whose tree is in a render answer; <paramref name="Draft"/> when it is the session's draft.</summary>
public sealed record ModRenderedBy(string Name, bool Draft);

/// <param name="Tree">A <c>WireElement</c>; null when no mod draws here (the browser draws Fleet's own).</param>
public sealed record ModRenderView(JsonElement? Tree, IReadOnlyList<ModRenderedBy> Mods);

/// <summary>Which tool-row sites a session's mods hook, so the browser asks only about rows a mod could draw.</summary>
public sealed record ModSitesView(IReadOnlyList<ModSiteHooks> Sites);

/// <summary>Draws a session's tool rows through the user's mods.</summary>
public sealed class ModRenderService(IModHost host, IUserContext user)
{
    /// <summary>A row's <c>input</c> as JSON over this many bytes is replaced by <c>{}</c>.</summary>
    public const int MaxInputBytes = 64 * 1024;

    /// <summary>A row's <c>output</c> longer than this keeps its last characters.</summary>
    public const int MaxOutputChars = 256 * 1024;

    public ModSitesView SitesFor(string sessionId) => new(host.SitesFor(user.UserId, sessionId));

    public async Task<Result<ModRenderView>> RenderAsync(string sessionId, ModRenderRequest request, CancellationToken ct = default)
    {
        if (request.Site is not ("ToolUse" or "ToolResult"))
            return FleetError.ValidationError("Site", "\"site\" must be ToolUse or ToolResult.");
        if (string.IsNullOrEmpty(request.RequestId))
            return FleetError.ValidationError("RequestId", "\"requestId\" is required: the tool call's id.");
        if (request.Props is not { ValueKind: JsonValueKind.Object } props)
            return FleetError.ValidationError("Props", "\"props\" must be an object.");

        var e = Event(request.Site, sessionId, request.RequestId, props);
        var answer = await host.DispatchAsync(user.UserId, new ModDispatchRequest("ui.render", sessionId, e), ct).ConfigureAwait(false);
        if (!answer.Dispatched || answer.Result is not { } result || IsFleet(result))
            return new ModRenderView(null, []);

        var mods = answer.DrawnBy.Select(id =>
        {
            var at = id.IndexOf('@', StringComparison.Ordinal);
            return new ModRenderedBy(at > 0 ? id[..at] : id, id.Contains("@draft:", StringComparison.Ordinal));
        }).ToList();
        return new ModRenderView(result.ValueKind == JsonValueKind.Null ? null : result, mods);
    }

    private static bool IsFleet(JsonElement result)
        => result is { ValueKind: JsonValueKind.Object } && result.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("Fleet");

    /// <summary>The event mods see, with the props capped: a large input becomes <c>{}</c>, a long output keeps its end.</summary>
    private static JsonElement Event(string site, string sessionId, string requestId, JsonElement props)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("component", site);
            writer.WriteString("sessionId", sessionId);
            writer.WriteString("requestId", requestId);
            writer.WritePropertyName("props");
            writer.WriteStartObject();
            foreach (var property in props.EnumerateObject())
            {
                if (property.Name is not ("input" or "inputTruncated" or "output" or "outputTruncated"))
                    property.WriteTo(writer);
            }

            WriteInput(writer, props);
            WriteOutput(writer, props);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteInput(Utf8JsonWriter writer, JsonElement props)
    {
        var truncated = props.TryGetProperty("inputTruncated", out var flag) && flag.ValueKind == JsonValueKind.True;
        if (props.TryGetProperty("input", out var input))
        {
            var size = new ArrayBufferWriter<byte>();
            using (var measure = new Utf8JsonWriter(size))
                input.WriteTo(measure);
            if (size.WrittenCount > MaxInputBytes)
            {
                writer.WriteStartObject("input");
                writer.WriteEndObject();
                truncated = true;
            }
            else
            {
                writer.WritePropertyName("input");
                input.WriteTo(writer);
            }
        }

        if (truncated || props.TryGetProperty("inputTruncated", out _))
            writer.WriteBoolean("inputTruncated", truncated);
    }

    private static void WriteOutput(Utf8JsonWriter writer, JsonElement props)
    {
        var truncated = props.TryGetProperty("outputTruncated", out var flag) && flag.ValueKind == JsonValueKind.True;
        if (props.TryGetProperty("output", out var output))
        {
            writer.WritePropertyName("output");
            if (output.ValueKind == JsonValueKind.String && output.GetString() is { Length: > MaxOutputChars } text)
            {
                var start = text.Length - MaxOutputChars;
                // Never start in the middle of a surrogate pair.
                if (char.IsLowSurrogate(text[start]))
                    start++;
                writer.WriteStringValue(text.AsSpan(start));
                truncated = true;
            }
            else
            {
                output.WriteTo(writer);
            }
        }

        if (truncated || props.TryGetProperty("outputTruncated", out _))
            writer.WriteBoolean("outputTruncated", truncated);
    }
}
