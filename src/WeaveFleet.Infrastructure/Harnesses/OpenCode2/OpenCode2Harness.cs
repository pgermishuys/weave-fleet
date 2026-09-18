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
    /// <remarks>Text turns only for now; each capability is turned on as Fleet maps the V2 events behind it.</remarks>
    public HarnessCapabilities Capabilities { get; } = new()
    {
        // The session exists in V2 before any prompt, so the first message is delivered like any other.
        RequiresInitialPrompt = false,
        SupportsStreaming = true,
        SupportsResume = true,
    };
}
