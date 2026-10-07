using System.Text.Json;

namespace WeaveFleet.Application.FleetTools;

/// <summary>One of Fleet's own tools as an MCP server lists it: its name, what the model reads about it, and its input.</summary>
/// <param name="Requires">The switch the tool needs (<see cref="FleetToolSwitches"/>), or null for a tool every session gets.</param>
public sealed record FleetToolDefinition(string Name, string Description, JsonElement InputSchema, string? Requires);

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

    /// <summary>The tools a session gets with <paramref name="switches"/>.</summary>
    public static IReadOnlyList<FleetToolDefinition> For(FleetToolSwitches switches) => [.. All.Where(tool => switches.Allows(tool.Requires))];

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
                tool.TryGetProperty("requires", out var requires) ? requires.GetString() : null)),
        ];
    }
}

/// <summary>
/// Which of Fleet's switchable tools a session gets, as the OpenCode plugins decide it from the switches their process
/// started with: <c>fleet_message</c> with messages between sessions on, <c>fleet_memory_*</c> with memory on,
/// <c>fleet_step_done</c> in a workflow step's session, and the agent's browser tools with Settings → Browser on.
/// </summary>
/// <param name="Walkthrough">The fleet-walkthrough skill is on: its page tool comes with it.</param>
public sealed record FleetToolSwitches(bool SessionMessages, bool Memory, bool WorkflowStep, bool Browser, bool Walkthrough = false)
{
    public const string SessionMessagesSwitch = "sessionMessages";
    public const string MemorySwitch = "memory";
    public const string WorkflowStepSwitch = "workflowStep";
    public const string BrowserSwitch = "browser";
    public const string WalkthroughSwitch = "walkthrough";

    /// <summary>The built-in skill whose page tool is behind <see cref="WalkthroughSwitch"/>.</summary>
    public const string WalkthroughSkill = "fleet-walkthrough";

    /// <summary>Whether a tool that needs <paramref name="requirement"/> is on; a switch Fleet doesn't know keeps its tool off.</summary>
    public bool Allows(string? requirement) => requirement switch
    {
        null => true,
        SessionMessagesSwitch => SessionMessages,
        MemorySwitch => Memory,
        WorkflowStepSwitch => WorkflowStep,
        BrowserSwitch => Browser,
        WalkthroughSwitch => Walkthrough,
        _ => false,
    };
}
