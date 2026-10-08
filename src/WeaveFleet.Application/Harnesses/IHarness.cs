using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Harnesses;

/// <summary>
/// Descriptor for a harness type — static metadata about what this harness is.
/// One instance per harness type is registered in DI.
/// </summary>
public interface IHarness
{
    /// <summary>Machine-readable type identifier, e.g. "opencode", "claude-code".</summary>
    string Type { get; }

    /// <summary>Human-readable display name, e.g. "OpenCode", "Claude Code".</summary>
    string DisplayName { get; }

    /// <summary>Declares what this harness supports.</summary>
    HarnessCapabilities Capabilities { get; }

    /// <summary>How the harness describes itself to the user.</summary>
    HarnessPresentation Presentation => DefaultPresentation;

    /// <summary>Its switches in Settings → Harnesses; none by default.</summary>
    IReadOnlyList<HarnessSetting> Settings => [];

    private static readonly HarnessPresentation DefaultPresentation = new();
}
