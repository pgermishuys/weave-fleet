using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeaveFleet.Application.FleetTools;

/// <summary>One of Fleet's own tools as an MCP server lists it: its name, what the model reads about it, and its input.</summary>
/// <param name="Requires">The switch the tool needs (<see cref="FleetToolSwitches"/>), or null for a tool every session gets.</param>
/// <param name="SwitchedProperties">
/// Inputs the tool has only with a switch on, by switch: <c>{"agentHandoff": {"machine": {…}}}</c>. With the switch off
/// the tool is exactly <paramref name="InputSchema"/>; with it on they're added after the others, and required unless
/// <paramref name="Optional"/> names them.
/// </param>
/// <param name="Optional">
/// Switched inputs a call may leave out: <c>machine</c> on <c>fleet_message</c> and <c>fleet_session_read</c>, where
/// leaving it out means this machine, as an empty string does. A harness checks calls against the schema it was given
/// (Claude Code, GitHub Copilot), so a required one fails before Fleet sees the call.
/// </param>
public sealed record FleetToolDefinition(
    string Name, string Description, JsonElement InputSchema, string? Requires, JsonElement SwitchedProperties = default, IReadOnlyList<string>? Optional = null);

/// <summary>
/// Fleet's own tools (<c>fleet_canvas_*</c>, <c>fleet_page_show</c>, <c>fleet_browser_*</c>, <c>fleet_session_read</c>, …)
/// for a harness that takes tools over MCP. They're read from <c>fleet-tools.json</c>, which holds the OpenCode plugin's
/// names, descriptions and inputs, so the model sees the same tools on every harness.
/// </summary>
public static class FleetToolCatalog
{
    public const string ResourceName = "fleet-tools.json";

    /// <summary>Every tool, in the order the plugin lists them.</summary>
    public static IReadOnlyList<FleetToolDefinition> All { get; } = Load();

    /// <summary>The tool named <paramref name="name"/>, or null when Fleet has none by that name.</summary>
    public static FleetToolDefinition? Find(string? name) => All.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    /// <summary>The tools a session gets with <paramref name="switches"/>, each with the inputs its switches give it.</summary>
    public static IReadOnlyList<FleetToolDefinition> For(FleetToolSwitches switches)
        => [.. All.Where(tool => switches.Allows(tool.Requires)).Select(tool => WithSwitchedProperties(tool, switches))];

    /// <summary>
    /// The tool with the inputs its switches turn on. A tool that gets none is the very one from the catalog, so a session
    /// without them is offered exactly what it was offered before those inputs existed.
    /// </summary>
    private static FleetToolDefinition WithSwitchedProperties(FleetToolDefinition tool, FleetToolSwitches switches)
    {
        if (tool.SwitchedProperties.ValueKind != JsonValueKind.Object)
            return tool;

        var added = tool.SwitchedProperties.EnumerateObject()
            .Where(group => switches.Allows(group.Name))
            .SelectMany(group => group.Value.EnumerateObject())
            .ToList();
        if (added.Count == 0)
            return tool;

        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        var required = schema["required"]!.AsArray();
        foreach (var property in added)
        {
            properties[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            if (tool.Optional?.Contains(property.Name, StringComparer.Ordinal) != true)
                required.Add((JsonNode)JsonValue.Create(property.Name));
        }

        return tool with { InputSchema = JsonSerializer.SerializeToElement(schema, FleetToolsJsonContext.Default.JsonObject) };
    }

    private static List<FleetToolDefinition> Load()
    {
        using var stream = typeof(FleetToolCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} isn't embedded in {typeof(FleetToolCatalog).Assembly.GetName().Name}.");
        using var document = JsonDocument.Parse(stream);
        return
        [
            .. document.RootElement.GetProperty("tools").EnumerateArray().Select(tool => new FleetToolDefinition(
                tool.GetProperty("name").GetString()!,
                tool.GetProperty("description").GetString()!,
                tool.GetProperty("inputSchema").Clone(),
                tool.TryGetProperty("requires", out var requires) ? requires.GetString() : null,
                tool.TryGetProperty("switchedProperties", out var switched) ? switched.Clone() : default,
                tool.TryGetProperty("optional", out var optional) ? [.. optional.EnumerateArray().Select(name => name.GetString()!)] : null)),
        ];
    }
}

/// <summary>
/// Which of Fleet's switchable tools a session gets, as the OpenCode plugins decide it from the switches their process
/// started with: <c>fleet_message</c> with messages between sessions on or a machine to hand work to, the hand-off tools
/// with a machine to hand work to,
/// <c>fleet_memory_*</c> with memory on, the <c>fleet_mod_*</c> tools with Mods on,
/// <c>fleet_step_done</c> in a workflow step's session, and the agent's browser tools with Settings → Permissions on.
/// </summary>
/// <param name="Walkthrough">The fleet-walkthrough skill is on: its page tool comes with it.</param>
/// <param name="AgentHandoff">A machine takes work from agents here (<c>AgentHandoffFeature</c>): its tools, and a machine on the message tools.</param>
/// <param name="Mods">Mods are switched on (<c>ModsFeature</c>): the agent's <c>fleet_mod_*</c> tools. Safe mode doesn't hide them.</param>
/// <param name="MemorySearch">Memory and "Search older notes" are on (<c>AgentMemorySearchFeature</c>): <c>fleet_memory_search</c>.</param>
/// <param name="PageChoice">
/// Pages that wait for the user's pick: <c>waitForChoice</c> on <c>fleet_page_show</c>, always offered. Fleet's
/// MCP server leaves it off for a harness that cuts a held call (<c>HarnessMcpCall.CantWait</c>).
/// </param>
public sealed record FleetToolSwitches(bool SessionMessages, bool Memory, bool WorkflowStep, bool Browser, bool Walkthrough = false, bool AgentHandoff = false, bool Mods = false, bool PageChoice = true, bool MemorySearch = false)
{
    public const string SessionMessagesSwitch = "sessionMessages";
    public const string AgentHandoffSwitch = "agentHandoff";
    public const string ModsSwitch = "mods";
    public const string MemorySwitch = "memory";
    public const string MemorySearchSwitch = "memorySearch";
    public const string WorkflowStepSwitch = "workflowStep";
    public const string BrowserSwitch = "browser";
    public const string WalkthroughSwitch = "walkthrough";
    public const string PageChoiceSwitch = "pageChoice";

    /// <summary>The built-in skill whose page tool is behind <see cref="WalkthroughSwitch"/>.</summary>
    public const string WalkthroughSkill = "fleet-walkthrough";

    /// <summary>
    /// Whether a tool that needs <paramref name="requirement"/> is on; a switch Fleet doesn't know keeps its tool off.
    /// <c>a|b</c> needs either: <c>fleet_message</c> comes with messages between sessions, or with a machine to hand work to.
    /// </summary>
    public bool Allows(string? requirement)
        => requirement?.Contains('|', StringComparison.Ordinal) == true
            ? requirement.Split('|').Any(Allows)
            : AllowsOne(requirement);

    private bool AllowsOne(string? requirement) => requirement switch
    {
        null => true,
        SessionMessagesSwitch => SessionMessages,
        MemorySwitch => Memory,
        MemorySearchSwitch => MemorySearch,
        WorkflowStepSwitch => WorkflowStep,
        BrowserSwitch => Browser,
        WalkthroughSwitch => Walkthrough,
        AgentHandoffSwitch => AgentHandoff,
        ModsSwitch => Mods,
        PageChoiceSwitch => PageChoice,
        _ => false,
    };
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class FleetToolsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
