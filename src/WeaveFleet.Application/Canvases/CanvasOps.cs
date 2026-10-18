using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeaveFleet.Application.Canvases;

public static class CanvasOpNames
{
    public const string SetPage = "setPage";
    public const string ShowPage = "showPage";
}

/// <summary>
/// One change to a canvas, recorded as a revision.
/// </summary>
public abstract record CanvasOp
{
    public abstract string Name { get; }
}

/// <summary>Points a browser canvas at a page. <see cref="AppId"/> is the Fleet-run app serving it, if any.</summary>
public sealed record SetPageOp(string Url, string? AppId) : CanvasOp
{
    public override string Name => CanvasOpNames.SetPage;
}

/// <summary>Points a page canvas at a copy Fleet made of a page the agent wrote. The whole state, every time.</summary>
public sealed record ShowPageOp(PageState Page) : CanvasOp
{
    public override string Name => CanvasOpNames.ShowPage;
}

/// <summary>
/// An accepted change: the new state JSON, the ops as they were applied (to store as the revision),
/// and a short summary for tool cards.
/// </summary>
public sealed record CanvasChange(string StateJson, IReadOnlyList<CanvasOp> Ops, string Summary);

/// <summary>
/// Builds, applies and serializes canvas change ops. A batch applies all-or-nothing, and the
/// resulting state is validated before it's accepted.
/// </summary>
public static class CanvasOps
{
    public static string EmptyState(string kind) => kind switch
    {
        CanvasKinds.Browser => new BrowserState().ToJson(),
        CanvasKinds.Page => new PageState().ToJson(),
        _ => throw new ArgumentException($"Unknown canvas kind \"{kind}\".", nameof(kind)),
    };

    /// <summary>
    /// Builds a new canvas from the state Fleet gives it. The state becomes ops applied to an empty canvas,
    /// so it's validated like any other change and the first revision records how it was built.
    /// </summary>
    public static CanvasResult<CanvasChange> Open(string kind, JsonNode? state)
    {
        if (!CanvasKinds.IsKnown(kind))
            return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, UnknownKindMessage(kind));

        CanvasOp op;
        try
        {
            op = kind == CanvasKinds.Browser ? BrowserOpFromState(Unwrap(state)) : PageOpFromState(Unwrap(state));
        }
        catch (CanvasFormatException ex)
        {
            return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, ex.Message);
        }

        return Apply(kind, EmptyState(kind), [op]);
    }

    /// <summary>
    /// Works out the ops that turn <paramref name="stateJson"/> into <paramref name="state"/>, for opening a
    /// title that already exists. The list is empty when nothing changes.
    /// </summary>
    public static CanvasResult<IReadOnlyList<CanvasOp>> Replace(string kind, string stateJson, JsonNode? state)
    {
        var target = Open(kind, state);
        if (!target.IsSuccess)
            return CanvasResult.Fail<IReadOnlyList<CanvasOp>>(target.Error);

        if (kind == CanvasKinds.Page)
        {
            // Every show is a new version, even of the same copy: the user's tab reloads on it.
            IReadOnlyList<CanvasOp> pageOps = [new ShowPageOp(PageState.Parse(target.Value.StateJson))];
            return CanvasResult.Ok(pageOps);
        }

        var page = BrowserState.Parse(target.Value.StateJson);
        var shown = BrowserState.Parse(stateJson);
        IReadOnlyList<CanvasOp> browserOps = shown.Url == page.Url && shown.AppId == page.AppId ? [] : [new SetPageOp(page.Url, page.AppId)];
        return CanvasResult.Ok(browserOps);
    }

    /// <summary>
    /// Applies <paramref name="ops"/> to the canvas. Nothing is applied unless every op is one the kind
    /// takes and the resulting state is valid. Both kinds that remain take the whole state in every op.
    /// </summary>
    public static CanvasResult<CanvasChange> Apply(string kind, string stateJson, IReadOnlyList<CanvasOp> ops)
    {
        if (!CanvasKinds.IsKnown(kind))
            return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, UnknownKindMessage(kind));
        if (ops.Count == 0)
            return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, "No changes given.");

        var allowed = kind == CanvasKinds.Browser ? CanvasOpNames.SetPage : CanvasOpNames.ShowPage;
        foreach (var op in ops)
        {
            if (op.Name != allowed)
                return CanvasResult.Fail<CanvasChange>(CanvasErrorKind.Invalid, $"{op.Name} doesn't apply to a {kind} canvas.");
        }

        return kind == CanvasKinds.Browser ? ApplyBrowser(ops) : ApplyPage(ops);
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

    private static CanvasResult<CanvasChange> ApplyPage(IReadOnlyList<CanvasOp> ops)
    {
        var state = new PageState();
        foreach (var op in ops)
        {
            if (op is ShowPageOp show)
                state = show.Page;
        }

        if (PageStateValidator.Validate(state) is { } invalid)
            return CanvasResult.Fail<CanvasChange>(invalid);

        var json = state.ToJson();
        if (CanvasValidators.CheckSize(json) is { } tooLarge)
            return CanvasResult.Fail<CanvasChange>(tooLarge);

        return CanvasResult.Ok<CanvasChange>(new CanvasChange(json, ops, CanvasText.Summarize(ops)));
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
                    case SetPageOp page:
                        writer.WriteString("url", page.Url);
                        WriteOptional(writer, "appId", page.AppId);
                        break;
                    case ShowPageOp show:
                        writer.WriteString("pageId", show.Page.PageId);
                        writer.WriteString("entry", show.Page.Entry);
                        writer.WriteString("source", show.Page.Source);
                        writer.WriteNumber("files", show.Page.Files);
                        writer.WriteNumber("bytes", show.Page.Bytes);
                        writer.WriteString("shownAt", show.Page.ShownAt);
                        writer.WriteStartArray("warnings");
                        foreach (var warning in show.Page.Warnings)
                            writer.WriteStringValue(warning);
                        writer.WriteEndArray();
                        break;
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static SetPageOp BrowserOpFromState(JsonNode? state)
    {
        if (state is not JsonObject root)
            throw new CanvasFormatException("\"state\" must be an object with \"url\".");

        var reader = new FieldReader(root, "state");
        reader.AllowOnly("url", "appId");
        return new SetPageOp(reader.Page("url"), reader.OptionalText("appId"));
    }

    private static ShowPageOp PageOpFromState(JsonNode? state)
    {
        if (state is not JsonObject root)
            throw new CanvasFormatException("\"state\" must be an object with \"pageId\", \"entry\" and \"source\".");

        return new ShowPageOp(ReadPage(new FieldReader(root, "state")));
    }

    private static PageState ReadPage(FieldReader r)
    {
        r.AllowOnly("pageId", "entry", "source", "files", "bytes", "shownAt", "warnings", "label");
        return new PageState
        {
            PageId = r.Page("pageId"),
            Entry = r.Page("entry"),
            Source = r.Page("source"),
            Files = (int)r.Number("files"),
            Bytes = (long)r.Number("bytes"),
            ShownAt = r.Page("shownAt"),
            Warnings = r.OptionalStrings("warnings"),
            Label = r.OptionalNonEmptyText("label"),
        };
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

    private static string UnknownKindMessage(string kind)
        => $"Unknown canvas kind \"{kind}\". Use {CanvasKinds.Browser} or {CanvasKinds.Page}.";

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
            writer.WriteString(name, value);
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
                throw Error($"unknown field \"{key}\". Allowed: {string.Join(", ", names)}.");
            }
        }

        public string? OptionalNonEmptyText(string name)
        {
            var value = OptionalText(name);
            if (value is { Length: 0 })
                throw Error($"\"{name}\" can't be empty.");
            return value;
        }

        public string? OptionalText(string name) => ReadString(name, required: false)?.Trim();

        /// <summary>A browser page address. Empty is allowed here; the state validator decides when (a starting app).</summary>
        public string Page(string name) => ReadString(name, required: true)!.Trim();

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
