using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.Pi;

/// <summary>
/// Opaque launch artifacts for the Pi harness.
/// Carries resolved launch provider/model and environment variables from preparation to spawn/resume.
/// </summary>
internal sealed record PiLaunchArtifacts(
    string Provider,
    string Model,
    IReadOnlyDictionary<string, string> EnvironmentVariables) : RuntimeLaunchArtifacts;
