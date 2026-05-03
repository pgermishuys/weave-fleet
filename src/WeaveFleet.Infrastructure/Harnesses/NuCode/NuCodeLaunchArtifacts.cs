using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Opaque launch artifacts for the NuCode harness.
/// Carries the resolved API key and provider/model info from PrepareRuntimeAsync to SpawnAsync.
/// </summary>
/// <param name="Provider">Provider identifier (e.g. "anthropic", "openai", "copilot").</param>
/// <param name="ModelId">The model identifier.</param>
/// <param name="ApiKey">The API key (for anthropic/openai) or empty for copilot.</param>
/// <param name="GitHubToken">GitHub OAuth token for Copilot token exchange (null for non-copilot providers).</param>
public sealed record NuCodeLaunchArtifacts(
    string Provider,
    string ModelId,
    string ApiKey,
    string? GitHubToken = null) : RuntimeLaunchArtifacts;
