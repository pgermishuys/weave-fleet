using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Descriptor-only <see cref="IHarness"/> implementation for the OpenCode AI coding agent.
/// Provides static metadata: type identifier, display name, and capabilities.
/// Runtime provisioning is handled by <see cref="OpenCodeHarnessRuntime"/>.
/// </summary>
public sealed class OpenCodeHarness : IHarness
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
    };
}
