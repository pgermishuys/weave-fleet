using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Launch artifacts for OpenCode 2. Empty: V2 keeps its own provider sign-ins, and a session runs on its owner's
/// server whatever it was prepared with.
/// </summary>
internal sealed record OpenCode2LaunchArtifacts : RuntimeLaunchArtifacts;
