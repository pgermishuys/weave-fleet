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
        SupportsForking = false,
        SupportsResume = true,
        SupportsImageAttachments = false,
        SupportsStreaming = true,
        SupportsDelegation = false,
    };
}
