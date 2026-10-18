using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeaveFleet.Application.Canvases;

internal static class CanvasJson
{
    // Canvas JSON is never embedded in HTML, so keep quotes and arrows in it readable.
    public static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>
/// The state of a <see cref="CanvasKinds.Browser"/> canvas: the page to show and, when Fleet started the
/// app, the run it belongs to. The proxy address isn't stored; it changes every time Fleet starts.
/// <see cref="Url"/> is empty while that app is starting and hasn't served a page yet.
/// </summary>
public sealed class BrowserState
{
    public string Url { get; set; } = string.Empty;
    public string? AppId { get; set; }

    public static BrowserState Parse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("Browser state is not a JSON object.");
        return new BrowserState { Url = (string?)root["url"] ?? string.Empty, AppId = (string?)root["appId"] };
    }

    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CanvasJson.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("url", Url);
            if (AppId is not null)
                writer.WriteString("appId", AppId);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// The state of a <see cref="CanvasKinds.Page"/> canvas: the page Fleet serves at <c>/pages/{PageId}/{Entry}</c>, the
/// file it was copied from, what the copy took, and links in it that won't load. <see cref="ShownAt"/> changes on
/// every show, so showing the same file again is a new version and the user's tab reloads.
/// </summary>
public sealed class PageState
{
    public string PageId { get; set; } = string.Empty;
    public string Entry { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int Files { get; set; }
    public long Bytes { get; set; }
    public string ShownAt { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = [];

    /// <summary>What the tab's bar says in place of <see cref="Source"/>, for a page Fleet wrote itself (a walkthrough).</summary>
    public string? Label { get; set; }

    /// <summary>
    /// The pick the agent waits for on this page (<c>waitForChoice</c>), and how the wait ended. Only Fleet sets it:
    /// an agent's state for a page never carries one.
    /// </summary>
    public PageChoiceState? Choice { get; set; }

    public static PageState Parse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("Page state is not a JSON object.");
        return new PageState
        {
            PageId = (string?)root["pageId"] ?? string.Empty,
            Entry = (string?)root["entry"] ?? string.Empty,
            Source = (string?)root["source"] ?? string.Empty,
            Files = (int?)root["files"] ?? 0,
            Bytes = (long?)root["bytes"] ?? 0,
            ShownAt = (string?)root["shownAt"] ?? string.Empty,
            Warnings = root["warnings"] is JsonArray warnings ? [.. warnings.Select(item => (string?)item).OfType<string>()] : [],
            Label = (string?)root["label"],
            Choice = root["choice"] is JsonObject choice ? PageChoiceState.Read(choice) : null,
        };
    }

    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CanvasJson.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("pageId", PageId);
            writer.WriteString("entry", Entry);
            writer.WriteString("source", Source);
            writer.WriteNumber("files", Files);
            writer.WriteNumber("bytes", Bytes);
            writer.WriteString("shownAt", ShownAt);
            writer.WriteStartArray("warnings");
            foreach (var warning in Warnings)
                writer.WriteStringValue(warning);
            writer.WriteEndArray();
            if (Label is not null)
                writer.WriteString("label", Label);
            Choice?.Write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// A page's wait for the user's pick: <see cref="Waiting"/> while the tool call is open, then <see cref="Answered"/>
/// with what the user sent, or <see cref="NotAnswered"/> with why (<c>PageChoiceReasons</c>).
/// </summary>
public sealed record PageChoiceState(string CallId, string Status, string At, string? Value = null, string? Summary = null, string? Reason = null)
{
    public const string Waiting = "waiting";
    public const string Answered = "answered";
    public const string NotAnswered = "not-answered";

    internal static PageChoiceState? Read(JsonObject choice)
        => (string?)choice["callId"] is { Length: > 0 } callId && (string?)choice["status"] is { Length: > 0 } status
            ? new PageChoiceState(callId, status, (string?)choice["at"] ?? string.Empty, (string?)choice["value"], (string?)choice["summary"], (string?)choice["reason"])
            : null;

    internal void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("choice");
        writer.WriteString("callId", CallId);
        writer.WriteString("status", Status);
        writer.WriteString("at", At);
        if (Value is not null)
            writer.WriteString("value", Value);
        if (Summary is not null)
            writer.WriteString("summary", Summary);
        if (Reason is not null)
            writer.WriteString("reason", Reason);
        writer.WriteEndObject();
    }
}
