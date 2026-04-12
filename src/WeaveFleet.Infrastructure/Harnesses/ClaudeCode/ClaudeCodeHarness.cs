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
        RequiresInitialPrompt = true,
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
