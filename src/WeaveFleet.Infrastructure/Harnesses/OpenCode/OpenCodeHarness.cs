using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Descriptor-only <see cref="IHarness"/> implementation for the OpenCode AI coding agent.
/// Provides static metadata: type identifier, display name, and capabilities.
/// Runtime provisioning is handled by <see cref="OpenCodeHarnessRuntime"/>.
/// </summary>
public sealed class OpenCodeHarness(FleetOptions options) : IHarness
{
    /// <inheritdoc />
    public string Type => "opencode";

    /// <inheritdoc />
    public string DisplayName => "OpenCode";

    /// <inheritdoc />
    public HarnessCapabilities Capabilities { get; } = new()
    {
        RequiresInitialPrompt = false,
        SupportsAgents = true,
        SupportsModelSelection = true,
        SupportsCommands = true,
        SupportsForking = true,
        SupportsResume = true,
        SupportsImageAttachments = true,
        SupportsStreaming = true,
        SupportsDelegation = true,
        ReportsTodos = true,
        ReportsFileWrites = true,
        SupportsOffTheRecordPrompt = true,
        SupportsWorkflowSteps = true,
        SupportsProfiles = true,
        HistoryLivesInHarness = true,
        // A prompt sent while a turn runs is read at the turn's next model call.
        SupportsSteering = true,
        // POST /session/{id}/shell, between turns (OpenCode refuses one while a turn runs).
        SupportsShellCommands = true,
        SupportsCompaction = true,
        SupportsSideConversations = true,
        // Synthetic text parts ahead of the prompt.
        TakesModelNotes = true,
        SupportsAgentBrowser = true,
        // Fleet's plugin gives the agent its tools, fleet_session_read among them.
        SupportsFleetTools = true,
        // Subagents are running work in child sessions; OpenCode has no background shells.
        SupportsChildSessions = true,
        ChildSessionsResumable = true,
        // Its permission config asks, and Fleet's plugin answers by the level.
        SupportsPermissionLevels = true,
    };

    /// <inheritdoc />
    public HarnessPresentation Presentation { get; } = new()
    {
        Order = 1,
        Eyebrow = "CLI harness",
        Description = "Harness for sessions backed by the OpenCode command-line runtime.",
        Pitch = "Open source. Includes free models, or sign in to your own provider.",
        Icon = HarnessIcons.Terminal,
        PermissionModes = new(
            Ask: "Fleet allows reading and asks you about the rest",
            Edits: "Fleet allows reading and edits, and asks you about the rest",
            All: "Fleet allows everything OpenCode asks about"),
        AgentBrowser = "Fleet's browser tools: read the page, act on it.",
        ProfileNote = "Fleet hands this to OpenCode as `OPENCODE_CONFIG`, on top of your own opencode.json. Fleet's own "
            + "settings (every tool allowed, the Fleet plugin) still apply after it. Keep API keys in Credentials and refer "
            + "to them with `{env:NAME}`.",
    };

    /// <inheritdoc />
    /// <remarks>Read on every request: <c>PUT /api/config</c> can change the default while Fleet runs.</remarks>
    public IReadOnlyList<HarnessSetting> Settings =>
    [
        new(
            OpenCodeFeatureFlagProvider.PooledOpenCodeHarnessPreferenceKey,
            "Pooled OpenCode Mode",
            "New OpenCode sessions run on a shared OpenCode process that Fleet starts when they need it, so they take a "
                + "prompt after Fleet restarts without a manual Resume. Sessions that already exist keep their mode.",
            options.Harness.PooledOpenCodeHarness),
    ];
}
