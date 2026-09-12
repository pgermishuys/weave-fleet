using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeaveFleet.Application.Canvases;

internal static class CanvasJson
{
    // Canvas JSON is never embedded in HTML, so keep arrows and quotes in Mermaid source and labels readable.
    public static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

public static class DiagramEdgeStyles
{
    public const string Solid = "solid";
    public const string Dashed = "dashed";
    public const string Planned = "planned";

    public static readonly IReadOnlyList<string> All = [Solid, Dashed, Planned];
}

public static class DiagramDirections
{
    public const string Default = "TB";

    public static readonly IReadOnlyList<string> All = ["TB", "LR", "BT", "RL"];
}

public sealed class DiagramNode
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Detail { get; set; }
    /// <summary>Set only when the user has placed the box. The client lays out boxes without a position.</summary>
    public double? X { get; set; }
    public double? Y { get; set; }
    public bool PlacedByUser { get; set; }
}

public sealed class DiagramEdge
{
    public string Id { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string Style { get; set; } = DiagramEdgeStyles.Solid;
}

/// <summary>
/// The state of a <see cref="CanvasKinds.Diagram"/> canvas. Stored as JSON written by <see cref="ToJson"/>.
/// </summary>
public sealed class DiagramState
{
    public string Direction { get; set; } = DiagramDirections.Default;
    public List<DiagramNode> Nodes { get; } = [];
    public List<DiagramEdge> Edges { get; } = [];

    public DiagramNode? FindNode(string id) => Nodes.Find(node => string.Equals(node.Id, id, StringComparison.Ordinal));

    public DiagramEdge? FindEdge(string id) => Edges.Find(edge => string.Equals(edge.Id, id, StringComparison.Ordinal));

    /// <summary>Reads state written by <see cref="ToJson"/>.</summary>
    public static DiagramState Parse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("Diagram state is not a JSON object.");
        var state = new DiagramState
        {
            Direction = (string?)root["direction"] ?? DiagramDirections.Default,
        };

        if (root["nodes"] is JsonArray nodes)
        {
            foreach (var item in nodes)
            {
                var node = item!.AsObject();
                state.Nodes.Add(new DiagramNode
                {
                    Id = (string)node["id"]!,
                    Label = (string)node["label"]!,
                    Detail = (string?)node["detail"],
                    X = (double?)node["x"],
                    Y = (double?)node["y"],
                    PlacedByUser = (bool?)node["placedByUser"] ?? false,
                });
            }
        }

        if (root["edges"] is JsonArray edges)
        {
            foreach (var item in edges)
            {
                var edge = item!.AsObject();
                state.Edges.Add(new DiagramEdge
                {
                    Id = (string)edge["id"]!,
                    From = (string)edge["from"]!,
                    To = (string)edge["to"]!,
                    Label = (string?)edge["label"],
                    Style = (string?)edge["style"] ?? DiagramEdgeStyles.Solid,
                });
            }
        }

        return state;
    }

    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CanvasJson.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("direction", Direction);

            writer.WriteStartArray("nodes");
            foreach (var node in Nodes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", node.Id);
                writer.WriteString("label", node.Label);
                if (node.Detail is not null)
                    writer.WriteString("detail", node.Detail);
                if (node.X is { } x)
                    writer.WriteNumber("x", x);
                if (node.Y is { } y)
                    writer.WriteNumber("y", y);
                if (node.PlacedByUser)
                    writer.WriteBoolean("placedByUser", true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("edges");
            foreach (var edge in Edges)
            {
                writer.WriteStartObject();
                writer.WriteString("id", edge.Id);
                writer.WriteString("from", edge.From);
                writer.WriteString("to", edge.To);
                if (edge.Label is not null)
                    writer.WriteString("label", edge.Label);
                writer.WriteString("style", edge.Style);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// The state of a <see cref="CanvasKinds.Sequence"/> canvas: Mermaid source.
/// </summary>
public sealed class SequenceState
{
    public string Source { get; set; } = string.Empty;

    public static SequenceState Parse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("Sequence state is not a JSON object.");
        return new SequenceState { Source = (string?)root["source"] ?? string.Empty };
    }

    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CanvasJson.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("source", Source);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
