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

/// <summary>A control in a mod's tree was used (<c>docs/mods/api.md</c>, "Fleet and the browser").</summary>
/// <param name="Handle">The control's handle from the tree.</param>
/// <param name="Kind"><c>press</c>, <c>input</c>, <c>submit</c> or <c>select</c>.</param>
/// <param name="Value">What an <c>Input</c> or <c>Select</c> holds.</param>
/// <param name="Surface"><c>desktop</c> (the default) or <c>phone</c>.</param>
public sealed record ModActionRequest(string? Handle, string? Kind, string? Value, string? Surface);

/// <param name="Handled">False when no mod could take it: the handle is unknown or expired (the tree was drawn again, or
/// the mod reloaded) or the host isn't running. The browser draws the site again.</param>
public sealed record ModActionView(bool Handled);

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

    /// <summary>A control was used: the host finds its mod and callback from the handle, and the callback runs.</summary>
    public async Task<Result<ModActionView>> ActAsync(string sessionId, ModActionRequest request, CancellationToken ct = default)
    {
        var (ev, kind) = request.Kind switch
        {
            "press" => ("ui.press", "press"),
            "input" => ("ui.input", "change"),
            "submit" => ("ui.input", "submit"),
            "select" => ("ui.select", "select"),
            _ => (null, null),
        };
        if (ev is null)
            return FleetError.ValidationError("Kind", "\"kind\" must be press, input, submit or select.");
        if (string.IsNullOrEmpty(request.Handle))
            return FleetError.ValidationError("Handle", "\"handle\" is required: the control's handle from the tree.");
        if (request.Surface is not (null or "desktop" or "phone"))
            return FleetError.ValidationError("Surface", "\"surface\" must be desktop or phone.");
        var surface = request.Surface ?? "desktop";

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", sessionId);
            writer.WriteString("handle", request.Handle);
            writer.WriteString("surface", surface);
            if (ev == "ui.input")
                writer.WriteString("kind", kind);
            if (request.Value is not null)
                writer.WriteString("value", request.Value);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        var answer = await host.DispatchAsync(user.UserId, new ModDispatchRequest(ev, sessionId, document.RootElement.Clone(), surface), ct).ConfigureAwait(false);
        return new ModActionView(answer.Dispatched);
    }

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
