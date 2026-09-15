using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// Descriptor-only <see cref="IHarness"/> implementation for the Claude Code AI coding agent.
/// Provides static metadata: type identifier, display name, and capabilities.
/// Runtime provisioning is handled by <see cref="ClaudeCodeHarnessRuntime"/>.
/// </summary>
public sealed class ClaudeCodeHarness : IHarness
{
    /// <inheritdoc />
    public string Type => "claude-code";

    /// <inheritdoc />
    public string DisplayName => "Claude Code";

    /// <inheritdoc />
    public HarnessCapabilities Capabilities { get; } = new()
    {
        // Each prompt starts its own claude process, so the session can exist before the first one.
        // The first message is then saved and delivered like any other.
        RequiresInitialPrompt = false,
        SupportsAgents = false,
        SupportsModelSelection = true,
        SupportsCommands = false,
        SupportsForking = false,
        SupportsResume = true,
        SupportsImageAttachments = false,
        SupportsStreaming = true,
        SupportsDelegation = false,
    };
}
