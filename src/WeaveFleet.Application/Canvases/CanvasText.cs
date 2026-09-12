using System.Text;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Canvases;

/// <summary>
/// The short text the agent reads back from canvas tools. It never includes positions: layout is the
/// user's, and leaving it out keeps reads cheap.
/// </summary>
public static class CanvasText
{
    public const string ReadHint = "Call fleet_canvas_read to see the current diagram.";

    /// <summary>
    /// The whole canvas as compact text, for <c>fleet_canvas_read</c> with <c>full: true</c>:
    /// <code>
    /// diagram cv_01J… "Session event flow" v5 TB
    /// n1 NuCode session · NuCode/Sessions
    /// e1 n1 -> n2 publishes
    /// e9 n3 -> n5 SessionListChanged [planned]
    /// </code>
    /// </summary>
    public static string RenderFull(Canvas canvas)
    {
        var text = new StringBuilder();
        text.Append(canvas.Kind).Append(' ').Append(canvas.Id).Append(' ').Append(Quote(canvas.Title))
            .Append(" v").Append(canvas.Version);

        if (canvas.Kind == CanvasKinds.Sequence)
        {
            text.Append('\n').Append(SequenceState.Parse(canvas.StateJson).Source);
            return text.ToString();
        }

        var state = DiagramState.Parse(canvas.StateJson);
        text.Append(' ').Append(state.Direction);
        if (state.Nodes.Count == 0)
            text.Append("\n(no boxes)");

        foreach (var node in state.Nodes)
        {
            text.Append('\n').Append(node.Id).Append(' ').Append(OneLine(node.Label));
            if (node.Detail is not null)
                text.Append(" · ").Append(OneLine(node.Detail));
        }

        foreach (var edge in state.Edges)
        {
            text.Append('\n').Append(edge.Id).Append(' ').Append(edge.From).Append(" -> ").Append(edge.To);
            if (edge.Label is not null)
                text.Append(' ').Append(OneLine(edge.Label));
            if (edge.Style != DiagramEdgeStyles.Solid)
                text.Append(" [").Append(edge.Style).Append(']');
        }

        return text.ToString();
    }

    /// <summary>
    /// What the user removed since the agent last looked, for <c>fleet_canvas_read</c> with <c>full: false</c>.
    /// Moves are layout and are never reported.
    /// </summary>
    public static string RenderChangesSince(int seenVersion, IReadOnlyList<CanvasUserRemoval> removals)
    {
        if (removals.Count == 0)
            return $"No changes since v{seenVersion}.";

        var text = new StringBuilder($"Since v{seenVersion} the user removed:");
        foreach (var removal in removals)
        {
            text.Append('\n').Append(BoxName(removal.Label, removal.NodeId));
            if (removal.EdgeIds.Count > 0)
                text.Append(removal.EdgeIds.Count == 1 ? ", with edge " : ", with edges ").AppendJoin(", ", removal.EdgeIds);
        }

        return text.ToString();
    }

    /// <summary>One line for <c>fleet_canvas_list</c>.</summary>
    public static string RenderListLine(Canvas canvas, bool changedByUser)
        => $"{canvas.Id} {canvas.Kind} {Quote(canvas.Title)} v{canvas.Version}{(changedByUser ? " (changed by user)" : string.Empty)}";

    /// <summary>A short summary of applied ops for tool cards, e.g. "+2 boxes, −1 edge".</summary>
    public static string Summarize(IReadOnlyList<CanvasOp> ops)
    {
        int addedBoxes = 0, removedBoxes = 0, editedBoxes = 0, addedEdges = 0, removedEdges = 0, editedEdges = 0;
        var movedBoxes = new HashSet<string>(StringComparer.Ordinal);
        string? direction = null;
        SetSourceOp? source = null;

        foreach (var op in ops)
        {
            switch (op)
            {
                case AddNodeOp: addedBoxes++; break;
                case UpdateNodeOp: editedBoxes++; break;
                case RemoveNodeOp remove:
                    removedBoxes++;
                    removedEdges += remove.RemovedEdgeIds.Count;
                    break;
                case AddEdgeOp: addedEdges++; break;
                case UpdateEdgeOp: editedEdges++; break;
                case RemoveEdgeOp: removedEdges++; break;
                case MoveNodeOp move: movedBoxes.Add(move.Id); break;
                case SetDirectionOp set: direction = set.Direction; break;
                case SetSourceOp set: source = set; break;
            }
        }

        var parts = new List<string>();
        if (addedBoxes > 0)
            parts.Add("+" + Plural(addedBoxes, "box"));
        if (removedBoxes > 0)
            parts.Add("−" + Plural(removedBoxes, "box"));
        if (editedBoxes > 0)
            parts.Add(Plural(editedBoxes, "box") + " edited");
        if (movedBoxes.Count > 0)
            parts.Add(Plural(movedBoxes.Count, "box") + " moved");
        if (addedEdges > 0)
            parts.Add("+" + Plural(addedEdges, "edge"));
        if (removedEdges > 0)
            parts.Add("−" + Plural(removedEdges, "edge"));
        if (editedEdges > 0)
            parts.Add(Plural(editedEdges, "edge") + " edited");
        if (direction is not null)
            parts.Add("direction " + direction);
        if (source is not null)
            parts.Add(Plural(source.Source.Split('\n').Length, "line"));

        return parts.Count == 0 ? "no changes" : string.Join(", ", parts);
    }

    /// <summary><c>"Session event flow" (cv_01J…)</c></summary>
    public static string CanvasName(Canvas canvas) => $"{Quote(canvas.Title)} ({canvas.Id})";

    /// <summary><c>box "use-sessions.ts" (n7)</c>, or <c>box n7</c> when the label isn't known.</summary>
    public static string BoxName(string? label, string id)
        => label is null ? $"box {id}" : $"box {Quote(OneLine(label))} ({id})";

    private static string Plural(int count, string noun)
        => count == 1 ? $"{count} {noun}" : $"{count} {noun}{(noun.EndsWith('x') ? "es" : "s")}";

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string OneLine(string value)
        => value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
}
