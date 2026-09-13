using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeaveFleet.Application.Canvases;

public static class CanvasOpNames
{
    public const string AddNode = "addNode";
    public const string UpdateNode = "updateNode";
    public const string RemoveNode = "removeNode";
    public const string AddEdge = "addEdge";
    public const string UpdateEdge = "updateEdge";
    public const string RemoveEdge = "removeEdge";
    public const string SetDirection = "setDirection";
    public const string MoveNode = "moveNode";
    public const string SetSource = "setSource";
    public const string SetPage = "setPage";
}

/// <summary>
/// One change to a canvas. Ops name boxes and edges by id, so the user's edits and the agent's
/// don't shift each other's references.
/// </summary>
public abstract record CanvasOp
{
    public abstract string Name { get; }
}

public sealed record AddNodeOp(string Id, string Label, string? Detail) : CanvasOp
{
    public override string Name => CanvasOpNames.AddNode;
}

/// <summary><c>null</c> leaves a field as it is. An empty <see cref="Detail"/> clears it.</summary>
public sealed record UpdateNodeOp(string Id, string? Label, string? Detail) : CanvasOp
{
    public override string Name => CanvasOpNames.UpdateNode;
}

/// <summary>
/// Removes a box and every edge to or from it. <see cref="RemovedLabel"/> and <see cref="RemovedEdgeIds"/>
/// are filled in when the op is applied, so the revision still knows what went after the box is gone.
/// </summary>
public sealed record RemoveNodeOp(string Id) : CanvasOp
{
    public override string Name => CanvasOpNames.RemoveNode;
    public string? RemovedLabel { get; init; }
    public IReadOnlyList<string> RemovedEdgeIds { get; init; } = [];
}

public sealed record AddEdgeOp(string Id, string From, string To, string? Label, string? Style) : CanvasOp
{
    public override string Name => CanvasOpNames.AddEdge;
}

/// <summary><c>null</c> leaves a field as it is. An empty <see cref="Label"/> clears it.</summary>
public sealed record UpdateEdgeOp(string Id, string? Label, string? Style) : CanvasOp
{
    public override string Name => CanvasOpNames.UpdateEdge;
}

public sealed record RemoveEdgeOp(string Id) : CanvasOp
{
    public override string Name => CanvasOpNames.RemoveEdge;
}

public sealed record SetDirectionOp(string Direction) : CanvasOp
{
    public override string Name => CanvasOpNames.SetDirection;
}

public sealed record MoveNodeOp(string Id, double X, double Y) : CanvasOp
{
    public override string Name => CanvasOpNames.MoveNode;
}

public sealed record SetSourceOp(string Source) : CanvasOp
{
    public override string Name => CanvasOpNames.SetSource;
}

/// <summary>Points a browser canvas at a page. <see cref="AppId"/> is the Fleet-run app serving it, if any.</summary>
public sealed record SetPageOp(string Url, string? AppId) : CanvasOp
{
    public override string Name => CanvasOpNames.SetPage;
}

/// <summary>
/// An accepted change: the new state JSON, the ops as they were applied (to store as the revision),
/// and a short summary for tool cards, e.g. "+2 boxes, −1 edge".
/// </summary>
public sealed record CanvasChange(string StateJson, IReadOnlyList<CanvasOp> Ops, string Summary);

/// <summary>
/// Parses, applies and serializes canvas change ops. A batch applies all-or-nothing, and the
/// resulting state is validated before it's accepted.
/// </summary>
public static class CanvasOps
{
    private static readonly string[] AgentDiagramOps =
    [
        CanvasOpNames.AddNode, CanvasOpNames.UpdateNode, CanvasOpNames.RemoveNode,
        CanvasOpNames.AddEdge, CanvasOpNames.UpdateEdge, CanvasOpNames.RemoveEdge,
        CanvasOpNames.SetDirection,
    ];

    private static readonly string[] UserDiagramOps = [CanvasOpNames.MoveNode, CanvasOpNames.RemoveNode];

    private static readonly string[] AgentSequenceOps = [CanvasOpNames.SetSource];

    private static readonly string[] AgentBrowserOps = [CanvasOpNames.SetPage];

    private static readonly string[] AllOps = [.. AgentDiagramOps, CanvasOpNames.MoveNode, .. AgentSequenceOps, .. AgentBrowserOps];

    public static IReadOnlyList<string> AllowedOps(string kind, CanvasActor actor) => (kind, actor) switch
    {
        (CanvasKinds.Diagram, CanvasActor.Agent) => AgentDiagramOps,
        (CanvasKinds.Diagram, CanvasActor.User) => UserDiagramOps,
        (CanvasKinds.Sequence, CanvasActor.Agent) => AgentSequenceOps,
        (CanvasKinds.Browser, CanvasActor.Agent) => AgentBrowserOps,
        _ => [],
    };

    public static string EmptyState(string kind) => kind switch
    {
        CanvasKinds.Diagram => new DiagramState().ToJson(),
        CanvasKinds.Sequence => new SequenceState().ToJson(),
        CanvasKinds.Browser => new BrowserState().ToJson(),
        _ => throw new ArgumentException($"Unknown canvas kind \"{kind}\".", nameof(kind)),
    };

    /// <summary>
    /// Parses a batch of ops sent by <paramref name="actor"/>. Accepts an array, a single op object,
    /// or either of those as a JSON string (models sometimes send objects as strings).
    /// </summary>
    public static CanvasResult<IReadOnlyList<CanvasOp>> Parse(string kind, CanvasActor actor, JsonNode? ops)
    {
        if (!CanvasKinds.IsKnown(kind))
            return CanvasResult.Fail<IReadOnlyList<CanvasOp>>(CanvasErrorKind.Invalid, UnknownKindMessage(kind));

        var allowed = AllowedOps(kind, actor);
        if (allowed.Count == 0)
            return CanvasResult.Fail<IReadOnlyList<CanvasOp>>(CanvasErrorKind.Invalid, $"The {ActorName(actor)} can't change a {kind} canvas.");

        var items = Unwrap(ops) switch
        {
            JsonArray array => array.ToList(),
            JsonObject single => [single],
            _ => null,
        };
        if (items is null || items.Count == 0)
            return CanvasResult.Fail<IReadOnlyList<CanvasOp>>(CanvasErrorKind.Invalid, "\"ops\" must be a non-empty array of changes.");

        try
        {
            var parsed = new List<CanvasOp>(items.Count);
            for (var i = 0; i < items.Count; i++)
                parsed.Add(ReadOp(items[i], i + 1, new OpRules(allowed, $"the {ActorName(actor)} on a {kind} canvas")));
            return CanvasResult.Ok<IReadOnlyList<CanvasOp>>(parsed);
        }
        catch (CanvasFormatException ex)
        {
            return CanvasResult.Fail<IReadOnlyList<CanvasOp>>(CanvasErrorKind.Invalid, ex.Message);
        }
    }

    /// <summary>
    /// Builds a new canvas from the state the agent sends with <c>fleet_canvas_open</c>. The state
    /// becomes agent ops applied to an empty canvas, so it's validated like any other change and the
    /// first revision records how it was built. Boxes can't carry positions.
    /// </summary>
    public static CanvasResult<CanvasChange> Open(string kind, JsonNode? state)
    {
        if (!CanvasKinds.IsKnown(kind))
            return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, UnknownKindMessage(kind));

        List<(CanvasOp Op, string Where)> ops;
        try
        {
            ops = kind switch
            {
                CanvasKinds.Diagram => DiagramOpsFromState(Unwrap(state)),
                CanvasKinds.Browser => BrowserOpsFromState(Unwrap(state)),
                _ => SequenceOpsFromState(Unwrap(state)),
            };
        }
        catch (CanvasFormatException ex)
        {
            return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, ex.Message);
        }

        return Apply(kind, CanvasActor.Agent, EmptyState(kind), ops.Select(o => o.Op).ToList(), ops.Select(o => o.Where).ToList());
    }

    /// <summary>
    /// Works out the agent ops that turn <paramref name="stateJson"/> into <paramref name="state"/>, for
    /// <c>fleet_canvas_open</c> on a title that already exists. <paramref name="state"/> is validated the
    /// same way as for <see cref="Open"/>. Boxes that stay keep the user's positions, and an edge whose
    /// ends change is removed and added again. The list is empty when nothing changes.
    /// </summary>
    public static CanvasResult<IReadOnlyList<CanvasOp>> Replace(string kind, string stateJson, JsonNode? state)
    {
        var target = Open(kind, state);
        if (!target.IsSuccess)
            return CanvasResult.Fail<IReadOnlyList<CanvasOp>>(target.Error);

        if (kind == CanvasKinds.Sequence)
        {
            var source = SequenceState.Parse(target.Value.StateJson).Source;
            IReadOnlyList<CanvasOp> sourceOps = SequenceState.Parse(stateJson).Source == source ? [] : [new SetSourceOp(source)];
            return CanvasResult.Ok(sourceOps);
        }

        if (kind == CanvasKinds.Browser)
        {
            var page = BrowserState.Parse(target.Value.StateJson);
            var shown = BrowserState.Parse(stateJson);
            IReadOnlyList<CanvasOp> pageOps = shown.Url == page.Url && shown.AppId == page.AppId ? [] : [new SetPageOp(page.Url, page.AppId)];
            return CanvasResult.Ok(pageOps);
        }

        var current = DiagramState.Parse(stateJson);
        var next = DiagramState.Parse(target.Value.StateJson);
        var ops = new List<CanvasOp>();

        if (current.Direction != next.Direction)
            ops.Add(new SetDirectionOp(next.Direction));

        // Removals go first so an id can move between a box and an edge.
        foreach (var edge in current.Edges)
        {
            var kept = next.FindEdge(edge.Id);
            if (kept is null || kept.From != edge.From || kept.To != edge.To)
                ops.Add(new RemoveEdgeOp(edge.Id));
        }

        foreach (var node in current.Nodes)
        {
            if (next.FindNode(node.Id) is null)
                ops.Add(new RemoveNodeOp(node.Id));
        }

        foreach (var node in next.Nodes)
        {
            var existing = current.FindNode(node.Id);
            if (existing is null)
            {
                ops.Add(new AddNodeOp(node.Id, node.Label, node.Detail));
            }
            else if (existing.Label != node.Label || existing.Detail != node.Detail)
            {
                ops.Add(new UpdateNodeOp(
                    node.Id,
                    existing.Label == node.Label ? null : node.Label,
                    existing.Detail == node.Detail ? null : node.Detail ?? string.Empty));
            }
        }

        foreach (var edge in next.Edges)
        {
            var existing = current.FindEdge(edge.Id);
            if (existing is null || existing.From != edge.From || existing.To != edge.To)
            {
                ops.Add(new AddEdgeOp(edge.Id, edge.From, edge.To, edge.Label, edge.Style));
            }
            else if (existing.Label != edge.Label || existing.Style != edge.Style)
            {
                ops.Add(new UpdateEdgeOp(
                    edge.Id,
                    existing.Label == edge.Label ? null : edge.Label ?? string.Empty,
                    existing.Style == edge.Style ? null : edge.Style));
            }
        }

        return CanvasResult.Ok<IReadOnlyList<CanvasOp>>(ops);
    }

    /// <summary>
    /// Applies <paramref name="ops"/> to <paramref name="stateJson"/>. Nothing is applied unless every op
    /// succeeds and the resulting state is valid. Doesn't check for conflicts with the user's edits;
    /// see <see cref="CanvasConflicts"/>.
    /// </summary>
    public static CanvasResult<CanvasChange> Apply(string kind, CanvasActor actor, string stateJson, IReadOnlyList<CanvasOp> ops)
        => Apply(kind, actor, stateJson, ops, where: null);

    private static CanvasResult<CanvasChange> Apply(
        string kind,
        CanvasActor actor,
        string stateJson,
        IReadOnlyList<CanvasOp> ops,
        IReadOnlyList<string>? where)
    {
        if (!CanvasKinds.IsKnown(kind))
            return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, UnknownKindMessage(kind));
        if (ops.Count == 0)
            return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, "No changes given.");

        var allowed = AllowedOps(kind, actor);
        for (var i = 0; i < ops.Count; i++)
        {
            if (!allowed.Contains(ops[i].Name))
                return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, $"{Where(where, ops, i)}: the {ActorName(actor)} can't use {ops[i].Name} on a {kind} canvas.");
        }

        return kind switch
        {
            CanvasKinds.Diagram => ApplyDiagram(DiagramState.Parse(stateJson), actor, ops, where),
            CanvasKinds.Browser => ApplyBrowser(ops),
            _ => ApplySequence(ops),
        };
    }

    private static CanvasResult<CanvasChange> ApplyDiagram(
        DiagramState state,
        CanvasActor actor,
        IReadOnlyList<CanvasOp> ops,
        IReadOnlyList<string>? where)
    {
        var hint = actor == CanvasActor.Agent ? " " + CanvasText.ReadHint : string.Empty;
        var applied = new List<CanvasOp>(ops.Count);

        for (var i = 0; i < ops.Count; i++)
        {
            var at = Where(where, ops, i);
            switch (ops[i])
            {
                case AddNodeOp add:
                    if (IsIdTaken(state, add.Id))
                        return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, $"{at}: id \"{add.Id}\" is already used.");
                    state.Nodes.Add(new DiagramNode { Id = add.Id, Label = add.Label, Detail = NullIfEmpty(add.Detail) });
                    applied.Add(add);
                    break;

                case UpdateNodeOp update:
                    {
                        var node = state.FindNode(update.Id);
                        if (node is null)
                            return UnknownBox(at, update.Id, hint);
                        if (update.Label is not null)
                            node.Label = update.Label;
                        if (update.Detail is not null)
                            node.Detail = NullIfEmpty(update.Detail);
                        applied.Add(update);
                        break;
                    }

                case RemoveNodeOp remove:
                    {
                        var node = state.FindNode(remove.Id);
                        if (node is null)
                            return UnknownBox(at, remove.Id, hint);
                        var edgeIds = state.Edges
                            .Where(edge => edge.From == remove.Id || edge.To == remove.Id)
                            .Select(edge => edge.Id)
                            .ToList();
                        state.Nodes.Remove(node);
                        state.Edges.RemoveAll(edge => edge.From == remove.Id || edge.To == remove.Id);
                        applied.Add(remove with { RemovedLabel = node.Label, RemovedEdgeIds = edgeIds });
                        break;
                    }

                case AddEdgeOp add:
                    if (IsIdTaken(state, add.Id))
                        return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, $"{at}: id \"{add.Id}\" is already used.");
                    if (state.FindNode(add.From) is null)
                        return UnknownBox(at, add.From, hint);
                    if (state.FindNode(add.To) is null)
                        return UnknownBox(at, add.To, hint);
                    state.Edges.Add(new DiagramEdge
                    {
                        Id = add.Id,
                        From = add.From,
                        To = add.To,
                        Label = NullIfEmpty(add.Label),
                        Style = add.Style ?? DiagramEdgeStyles.Solid,
                    });
                    applied.Add(add);
                    break;

                case UpdateEdgeOp update:
                    {
                        var edge = state.FindEdge(update.Id);
                        if (edge is null)
                            return UnknownEdge(at, update.Id, hint);
                        if (update.Label is not null)
                            edge.Label = NullIfEmpty(update.Label);
                        if (update.Style is not null)
                            edge.Style = update.Style;
                        applied.Add(update);
                        break;
                    }

                case RemoveEdgeOp remove:
                    {
                        var edge = state.FindEdge(remove.Id);
                        if (edge is null)
                            return UnknownEdge(at, remove.Id, hint);
                        state.Edges.Remove(edge);
                        applied.Add(remove);
                        break;
                    }

                case SetDirectionOp direction:
                    state.Direction = direction.Direction;
                    applied.Add(direction);
                    break;

                case MoveNodeOp move:
                    {
                        var node = state.FindNode(move.Id);
                        if (node is null)
                            return UnknownBox(at, move.Id, hint);
                        node.X = move.X;
                        node.Y = move.Y;
                        node.PlacedByUser = true;
                        applied.Add(move);
                        break;
                    }

                default:
                    return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, $"{at}: {ops[i].Name} doesn't apply to a diagram canvas.");
            }
        }

        if (DiagramStateValidator.Validate(state) is { } invalid)
            return CanvasResult.Fail<CanvasChange>(invalid);

        var json = state.ToJson();
        if (CanvasValidators.CheckSize(json) is { } tooLarge)
            return CanvasResult.Fail<CanvasChange>(tooLarge);

        return CanvasResult.Ok<CanvasChange>(new CanvasChange(json, applied, CanvasText.Summarize(applied)));
    }

    private static CanvasResult<CanvasChange> ApplySequence(IReadOnlyList<CanvasOp> ops)
    {
        var state = new SequenceState();
        foreach (var op in ops)
        {
            if (op is SetSourceOp set)
                state.Source = set.Source;
        }

        if (SequenceStateValidator.Validate(state) is { } invalid)
            return CanvasResult.Fail<CanvasChange>(invalid);

        var json = state.ToJson();
        if (CanvasValidators.CheckSize(json) is { } tooLarge)
            return CanvasResult.Fail<CanvasChange>(tooLarge);

        return CanvasResult.Ok<CanvasChange>(new CanvasChange(json, ops, CanvasText.Summarize(ops)));
    }

    private static CanvasResult<CanvasChange> ApplyBrowser(IReadOnlyList<CanvasOp> ops)
    {
        var state = new BrowserState();
        foreach (var op in ops)
        {
            if (op is SetPageOp set)
            {
                state.Url = set.Url;
                state.AppId = set.AppId;
            }
        }

        if (BrowserStateValidator.Validate(state) is { } invalid)
            return CanvasResult.Fail<CanvasChange>(invalid);

        return CanvasResult.Ok<CanvasChange>(new CanvasChange(state.ToJson(), ops, CanvasText.Summarize(ops)));
    }

    /// <summary>Serializes applied ops for <c>canvas_revisions.ops_json</c>.</summary>
    public static string ToJson(IReadOnlyList<CanvasOp> ops)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CanvasJson.WriterOptions))
        {
            writer.WriteStartArray();
            foreach (var op in ops)
            {
                writer.WriteStartObject();
                writer.WriteString("op", op.Name);
                switch (op)
                {
                    case AddNodeOp add:
                        writer.WriteString("id", add.Id);
                        writer.WriteString("label", add.Label);
                        WriteOptional(writer, "detail", add.Detail);
                        break;
                    case UpdateNodeOp update:
                        writer.WriteString("id", update.Id);
                        WriteOptional(writer, "label", update.Label);
                        WriteOptional(writer, "detail", update.Detail);
                        break;
                    case RemoveNodeOp remove:
                        writer.WriteString("id", remove.Id);
                        WriteOptional(writer, "removedLabel", remove.RemovedLabel);
                        writer.WriteStartArray("removedEdges");
                        foreach (var edgeId in remove.RemovedEdgeIds)
                            writer.WriteStringValue(edgeId);
                        writer.WriteEndArray();
                        break;
                    case AddEdgeOp add:
                        writer.WriteString("id", add.Id);
                        writer.WriteString("from", add.From);
                        writer.WriteString("to", add.To);
                        WriteOptional(writer, "label", add.Label);
                        WriteOptional(writer, "style", add.Style);
                        break;
                    case UpdateEdgeOp update:
                        writer.WriteString("id", update.Id);
                        WriteOptional(writer, "label", update.Label);
                        WriteOptional(writer, "style", update.Style);
                        break;
                    case RemoveEdgeOp remove:
                        writer.WriteString("id", remove.Id);
                        break;
                    case SetDirectionOp direction:
                        writer.WriteString("direction", direction.Direction);
                        break;
                    case MoveNodeOp move:
                        writer.WriteString("id", move.Id);
                        writer.WriteNumber("x", move.X);
                        writer.WriteNumber("y", move.Y);
                        break;
                    case SetSourceOp set:
                        writer.WriteString("source", set.Source);
                        break;
                    case SetPageOp page:
                        writer.WriteString("url", page.Url);
                        WriteOptional(writer, "appId", page.AppId);
                        break;
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Reads ops written by <see cref="ToJson"/>.</summary>
    public static IReadOnlyList<CanvasOp> ReadRecorded(string opsJson)
    {
        if (JsonNode.Parse(opsJson) is not JsonArray items)
            throw new FormatException("Recorded canvas ops are not a JSON array.");

        var ops = new List<CanvasOp>(items.Count);
        for (var i = 0; i < items.Count; i++)
            ops.Add(ReadOp(items[i], i + 1, OpRules.Recorded));
        return ops;
    }

    private static CanvasOp ReadOp(JsonNode? item, int number, OpRules rules)
    {
        if (item is not JsonObject obj)
            throw new CanvasFormatException($"Op {number} must be an object with an \"op\" field.");

        var name = obj["op"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
        if (name is null)
            throw new CanvasFormatException($"Op {number}: \"op\" is required. Use one of: {string.Join(", ", rules.Allowed)}.");
        if (!rules.Allowed.Contains(name))
        {
            throw new CanvasFormatException(AllOps.Contains(name)
                ? $"Op {number} ({name}): not allowed for {rules.Context}. Use one of: {string.Join(", ", rules.Allowed)}."
                : $"Op {number}: unknown op \"{name}\". Use one of: {string.Join(", ", rules.Allowed)}.");
        }

        var r = new FieldReader(obj, $"Op {number} ({name})");
        switch (name)
        {
            case CanvasOpNames.AddNode:
                r.AllowOnly("id", "label", "detail");
                return new AddNodeOp(r.Id("id"), r.Text("label"), r.OptionalText("detail"));

            case CanvasOpNames.UpdateNode:
                {
                    r.AllowOnly("id", "label", "detail");
                    var op = new UpdateNodeOp(r.Id("id"), r.OptionalNonEmptyText("label"), r.OptionalText("detail"));
                    if (op.Label is null && op.Detail is null)
                        throw r.Error("give \"label\" or \"detail\" to change.");
                    return op;
                }

            case CanvasOpNames.RemoveNode:
                if (!rules.IsRecorded)
                {
                    r.AllowOnly("id");
                    return new RemoveNodeOp(r.Id("id"));
                }
                return new RemoveNodeOp(r.Id("id"))
                {
                    RemovedLabel = r.OptionalText("removedLabel"),
                    RemovedEdgeIds = r.OptionalStrings("removedEdges"),
                };

            case CanvasOpNames.AddEdge:
                r.AllowOnly("id", "from", "to", "label", "style");
                return new AddEdgeOp(r.Id("id"), r.Id("from"), r.Id("to"), r.OptionalText("label"), r.OneOf("style", DiagramEdgeStyles.All, required: false));

            case CanvasOpNames.UpdateEdge:
                {
                    r.AllowOnly("id", "label", "style");
                    var op = new UpdateEdgeOp(r.Id("id"), r.OptionalText("label"), r.OneOf("style", DiagramEdgeStyles.All, required: false));
                    if (op.Label is null && op.Style is null)
                        throw r.Error("give \"label\" or \"style\" to change.");
                    return op;
                }

            case CanvasOpNames.RemoveEdge:
                r.AllowOnly("id");
                return new RemoveEdgeOp(r.Id("id"));

            case CanvasOpNames.SetDirection:
                r.AllowOnly("direction");
                return new SetDirectionOp(r.OneOf("direction", DiagramDirections.All, required: true)!);

            case CanvasOpNames.MoveNode:
                r.AllowOnly("id", "x", "y");
                return new MoveNodeOp(r.Id("id"), r.Number("x"), r.Number("y"));

            case CanvasOpNames.SetPage:
                r.AllowOnly("url", "appId");
                return new SetPageOp(r.Page("url"), r.OptionalText("appId"));

            default:
                r.AllowOnly("source");
                return new SetSourceOp(r.Source("source"));
        }
    }

    private static List<(CanvasOp Op, string Where)> DiagramOpsFromState(JsonNode? state)
    {
        if (state is not JsonObject root)
            throw new CanvasFormatException("\"state\" must be an object with \"nodes\" and \"edges\".");

        var reader = new FieldReader(root, "state");
        reader.AllowOnly("direction", "nodes", "edges");

        var ops = new List<(CanvasOp, string)>();
        var direction = reader.OneOf("direction", DiagramDirections.All, required: false);
        if (direction is not null && direction != DiagramDirections.Default)
            ops.Add((new SetDirectionOp(direction), "state.direction"));

        foreach (var (item, where) in reader.Items("nodes"))
        {
            if (item is not JsonObject node)
                throw new CanvasFormatException($"{where} must be an object with \"id\" and \"label\".");
            var r = new FieldReader(node, where);
            r.AllowOnly("id", "label", "detail");
            ops.Add((new AddNodeOp(r.Id("id"), r.Text("label"), r.OptionalText("detail")), where));
        }

        foreach (var (item, where) in reader.Items("edges"))
        {
            if (item is not JsonObject edge)
                throw new CanvasFormatException($"{where} must be an object with \"id\", \"from\" and \"to\".");
            var r = new FieldReader(edge, where);
            r.AllowOnly("id", "from", "to", "label", "style");
            ops.Add((new AddEdgeOp(r.Id("id"), r.Id("from"), r.Id("to"), r.OptionalText("label"), r.OneOf("style", DiagramEdgeStyles.All, required: false)), where));
        }

        if (ops.Count == 0)
            throw new CanvasFormatException("\"state\" has no boxes. Give at least one in \"nodes\".");

        return ops;
    }

    private static List<(CanvasOp Op, string Where)> SequenceOpsFromState(JsonNode? state)
    {
        // A plain string that isn't JSON is taken as the Mermaid source itself.
        if (state is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            return [(new SetSourceOp(value.GetValue<string>().Trim()), "state")];

        if (state is not JsonObject root)
            throw new CanvasFormatException("\"state\" must be an object with \"source\".");

        var reader = new FieldReader(root, "state");
        reader.AllowOnly("source");
        return [(new SetSourceOp(reader.Source("source")), "state.source")];
    }

    private static List<(CanvasOp Op, string Where)> BrowserOpsFromState(JsonNode? state)
    {
        if (state is not JsonObject root)
            throw new CanvasFormatException("\"state\" must be an object with \"url\".");

        var reader = new FieldReader(root, "state");
        reader.AllowOnly("url", "appId");
        return [(new SetPageOp(reader.Page("url"), reader.OptionalText("appId")), "state")];
    }

    /// <summary>Unwraps a JSON object or array that was sent as a string.</summary>
    private static JsonNode? Unwrap(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            return node;

        var text = value.GetValue<string>().Trim();
        if (!text.StartsWith('{') && !text.StartsWith('['))
            return node;

        try
        {
            return JsonNode.Parse(text) ?? node;
        }
        catch (JsonException)
        {
            return node;
        }
    }

    private static bool IsIdTaken(DiagramState state, string id)
        => state.FindNode(id) is not null || state.FindEdge(id) is not null;

    private static CanvasResult<CanvasChange> UnknownBox(string at, string id, string hint)
        => CanvasResult.Fail<CanvasChange>(CanvasErrorKind.UnknownId, $"{at}: no box with id \"{id}\".{hint}");

    private static CanvasResult<CanvasChange> UnknownEdge(string at, string id, string hint)
        => CanvasResult.Fail<CanvasChange>(CanvasErrorKind.UnknownId, $"{at}: no edge with id \"{id}\".{hint}");

    private static string Where(IReadOnlyList<string>? where, IReadOnlyList<CanvasOp> ops, int index)
        => where?[index] ?? $"Op {index + 1} ({ops[index].Name})";

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string ActorName(CanvasActor actor) => actor == CanvasActor.Agent ? "agent" : "user";

    private static string UnknownKindMessage(string kind)
        => $"Unknown canvas kind \"{kind}\". Use {CanvasKinds.Diagram} or {CanvasKinds.Sequence}.";

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
            writer.WriteString(name, value);
    }

    private sealed record OpRules(IReadOnlyList<string> Allowed, string Context)
    {
        public static readonly OpRules Recorded = new(AllOps, "recorded ops");

        public bool IsRecorded => ReferenceEquals(this, Recorded);
    }

    private sealed class CanvasFormatException(string message) : Exception(message);

    /// <summary>Reads fields of one JSON object strictly, with errors that say where the problem is.</summary>
    private sealed class FieldReader(JsonObject obj, string where)
    {
        public CanvasFormatException Error(string problem) => new($"{where}: {problem}");

        public void AllowOnly(params string[] names)
        {
            foreach (var (key, _) in obj)
            {
                if (key == "op" || names.Contains(key))
                    continue;
                if (key is "x" or "y" or "placedByUser")
                    throw Error("boxes can't carry positions. The user places them.");
                throw Error($"unknown field \"{key}\". Allowed: {string.Join(", ", names)}.");
            }
        }

        public string Id(string name)
        {
            var value = ReadString(name, required: true)!;
            if (!CanvasValidators.IsValidId(value))
                throw Error($"\"{name}\" must be 1-64 letters, digits, or _ . : - characters.");
            return value;
        }

        public string Text(string name)
            => OptionalNonEmptyText(name) ?? throw Error($"\"{name}\" is required.");

        public string? OptionalNonEmptyText(string name)
        {
            var value = OptionalText(name);
            if (value is { Length: 0 })
                throw Error($"\"{name}\" can't be empty.");
            return value;
        }

        public string? OptionalText(string name) => ReadString(name, required: false)?.Trim();

        public string Source(string name)
        {
            var value = ReadString(name, required: true)!.Trim();
            if (value.Length == 0)
                throw Error($"\"{name}\" can't be empty.");
            return value;
        }

        /// <summary>A browser page address. Empty is allowed here; the state validator decides when (a starting app).</summary>
        public string Page(string name) => ReadString(name, required: true)!.Trim();

        public string? OneOf(string name, IReadOnlyList<string> values, bool required)
        {
            var value = ReadString(name, required);
            if (value is null)
                return null;
            var match = values.FirstOrDefault(v => string.Equals(v, value.Trim(), StringComparison.OrdinalIgnoreCase));
            return match ?? throw Error($"\"{name}\" must be one of: {string.Join(", ", values)}.");
        }

        public double Number(string name)
        {
            var node = obj[name] ?? throw Error($"\"{name}\" is required.");
            if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
            {
                var number = value.GetValue<double>();
                if (double.IsFinite(number))
                    return number;
            }
            throw Error($"\"{name}\" must be a number.");
        }

        public List<string> OptionalStrings(string name)
            => obj[name] is JsonArray array ? array.Select(item => item?.GetValue<string>() ?? string.Empty).ToList() : [];

        public IEnumerable<(JsonNode? Item, string Where)> Items(string name)
        {
            var node = obj[name];
            if (node is null)
                return [];
            if (node is not JsonArray array)
                throw Error($"\"{name}\" must be an array.");
            return array.Select((item, index) => (item, $"{where}.{name}[{index}]"));
        }

        private string? ReadString(string name, bool required)
        {
            var node = obj[name];
            if (node is null)
                return required ? throw Error($"\"{name}\" is required.") : null;
            if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                return value.GetValue<string>();
            throw Error($"\"{name}\" must be a string.");
        }
    }
}
