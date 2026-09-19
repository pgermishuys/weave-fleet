using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Descriptor for OpenCode 2, a harness of its own next to OpenCode (1.x): V2 has a different server API, event
/// stream and plugin API. Runtime provisioning is handled by <see cref="OpenCode2HarnessRuntime"/>.
/// </summary>
public sealed class OpenCode2Harness : IHarness
{
    /// <inheritdoc />
    public string Type => OpenCode2HarnessSession.Type;

    /// <inheritdoc />
    public string DisplayName => "OpenCode 2";

    /// <inheritdoc />
    /// <remarks>Text, tools and questions; each capability is turned on as Fleet maps the V2 events behind it.</remarks>
    public HarnessCapabilities Capabilities { get; } = new()
    {
        // The session exists in V2 before any prompt, so the first message is delivered like any other.
        RequiresInitialPrompt = false,
        SupportsStreaming = true,
        SupportsResume = true,
        // A reopened session reads V2's own history.
        HistoryLivesInHarness = true,
        // The session keeps the agent and model a prompt picks; the catalog is read per folder.
        SupportsAgents = true,
        SupportsModelSelection = true,
        SupportsCommands = true,
        // A subagent's child session is a Fleet session under the parent.
        SupportsDelegation = true,
        // Recaps come from V2's generate, which leaves the session's history alone.
        SupportsOffTheRecordPrompt = true,
        // Pasted images go to V2 as prompt files, which it passes to the model as image input.
        SupportsImageAttachments = true,
        // Its edit and write tool calls are reported as the files they wrote.
        ReportsFileWrites = true,
    };
}
