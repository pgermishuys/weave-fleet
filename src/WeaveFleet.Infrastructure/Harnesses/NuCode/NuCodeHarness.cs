using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Descriptor-only <see cref="IHarness"/> implementation for the NuCode in-process AI coding agent.
/// Provides static metadata: type identifier, display name, and capabilities.
/// Runtime provisioning is handled by <see cref="NuCodeHarnessRuntime"/>.
/// </summary>
public sealed class NuCodeHarness : IHarness
{
    /// <inheritdoc />
    public string Type => "nucode";

    /// <inheritdoc />
    public string DisplayName => "NuCode";

    /// <inheritdoc />
    public HarnessCapabilities Capabilities { get; } = new()
    {
        RequiresInitialPrompt = true,
        SupportsAgents = true,
        SupportsModelSelection = true,
        SupportsCommands = false,
        SupportsForking = false,
        SupportsResume = true,
        SupportsImageAttachments = false,
        SupportsStreaming = true,
        SupportsDelegation = true,
    };
}
