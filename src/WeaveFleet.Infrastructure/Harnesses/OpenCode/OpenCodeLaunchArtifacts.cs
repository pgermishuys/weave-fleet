using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Harness-internal launch artifacts produced by <c>OpenCodeHarness.PrepareRuntimeAsync</c>.
/// Contains the environment variables to inject into the spawned opencode process.
/// Opaque to the application layer — only the OpenCode harness reads its contents.
/// </summary>
internal sealed record OpenCodeLaunchArtifacts(
    IReadOnlyDictionary<string, string> EnvironmentVariables) : RuntimeLaunchArtifacts;
