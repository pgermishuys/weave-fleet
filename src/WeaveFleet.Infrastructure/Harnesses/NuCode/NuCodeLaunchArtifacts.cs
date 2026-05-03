using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Opaque launch artifacts for the NuCode harness.
/// Carries the resolved API key and provider/model info from PrepareRuntimeAsync to SpawnAsync.
/// </summary>
public sealed record NuCodeLaunchArtifacts(
    string Provider,
    string ModelId,
    string ApiKey) : RuntimeLaunchArtifacts;
