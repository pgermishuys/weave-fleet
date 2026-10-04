using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.Pi;

/// <summary>
/// Descriptor-only <see cref="IHarness"/> implementation for the Pi AI coding agent.
/// Provides static metadata: type identifier, display name, and capabilities.
/// </summary>
public sealed class PiHarness : IHarness
{
    /// <inheritdoc />
    public string Type => "pi";

    /// <inheritdoc />
    public string DisplayName => "Pi";

    /// <inheritdoc />
    public HarnessCapabilities Capabilities { get; } = new()
    {
        RequiresInitialPrompt = true,
        SupportsAgents = false,
        SupportsModelSelection = true,
        SupportsCommands = false,
        // Pi's fork and clone switch the process they're sent to over to the copy, and a Fleet session is one Pi process: the
        // session would become the fork. Copying without that needs a second process, which Fleet doesn't start.
        SupportsForking = false,
        SupportsResume = true,
        SupportsImageAttachments = false,
        SupportsStreaming = true,
        SupportsDelegation = false,
        // Subagents come from Pi extensions (the example subagent extension), which run them inside the tool call: they
        // are reported as running work while the call runs, with no session of their own to open, and the call doesn't
        // return before they finish.
        ReportsBackgroundWork = false,
        SupportsChildSessions = false,
        ChildSessionsResumable = false,
        // Pi's RPC prompt is one user message, which Pi keeps in the history Fleet shows: there's no way to give the model
        // words of Fleet's own without a Fleet extension for Pi. So Pi isn't told about lost work yet.
        TakesModelNotes = false,
    };
}
