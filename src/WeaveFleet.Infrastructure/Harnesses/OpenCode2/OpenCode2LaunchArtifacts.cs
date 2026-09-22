using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Launch artifacts for OpenCode 2: the session's profile, which picks the server it runs on. V2 keeps its own provider
/// sign-ins, so there are no credentials here.
/// </summary>
internal sealed record OpenCode2LaunchArtifacts : RuntimeLaunchArtifacts
{
    /// <summary>The profile's file; <see langword="null"/> for a session without one, which runs on its owner's server.</summary>
    public OpenCode2Profile? Profile { get; init; }
}
