using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.Copilot;

/// <summary>
/// GitHub Copilot, through weave-copilot: an ACP agent built on GitHub's Copilot SDK that runs the Copilot CLI. Experimental
/// (Settings → Labs), so it is off until the user turns it on.
/// </summary>
public sealed class CopilotHarness : IHarness
{
    /// <summary>The harness type, as sessions and preferences name it.</summary>
    public const string TypeName = "copilot";

    /// <summary>The preference behind the Settings → Labs switch for GitHub Copilot.</summary>
    public const string FeaturePreferenceKey = "Copilot";

    /// <summary>The preference behind the HydraFusion switch in the harness's settings (off unless turned on).</summary>
    public const string HydraFusionPreferenceKey = "copilot.hydrafusion";

    /// <inheritdoc />
    public string Type => TypeName;

    /// <inheritdoc />
    public string DisplayName => "GitHub Copilot";

    /// <inheritdoc />
    public HarnessCapabilities Capabilities { get; } = new()
    {
        SupportsModelSelection = true,
        SupportsResume = true,
        SupportsStreaming = true,
        // Fleet's MCP server gives the agent Fleet's tools (CopilotFleetTools).
        SupportsFleetTools = true,
        SupportsSteering = true,
        SupportsPermissionLevels = true,
        ReportsFileWrites = true,
        HistoryLivesInHarness = true,
        SupportsForking = false,
        SupportsImageAttachments = true,
        ReportsTodos = true,
        SupportsProviderSignIn = true,
        TakesModelNotes = false,
    };

    /// <inheritdoc />
    public HarnessPresentation Presentation { get; } = new()
    {
        Order = 5,
        Eyebrow = "Experimental",
        Description = "GitHub Copilot through weave-copilot, with HydraFusion's passes when you turn them on.",
        Icon = HarnessIcons.Hexagon,
        PermissionModes = new(
            Ask: "Fleet allows reading and asks you about edits and commands",
            Edits: "Fleet allows reading and edits, and asks you about commands",
            All: "Fleet allows everything Copilot asks about"),
    };

    /// <inheritdoc />
    public IReadOnlyList<HarnessSetting> Settings { get; } =
    [
        new(
            HydraFusionPreferenceKey,
            "HydraFusion (research preview)",
            "Let Copilot choose a model or combine work from multiple models for your task. Try HydraFusion instead of "
                + "choosing a single model yourself. Research preview: experimental behavior may change. This uses GitHub's "
                + "experimental HydraFusion flag, and what a prompt costs varies a lot from one to the next. Applies to sessions "
                + "started afterwards.",
            false),
    ];
}
