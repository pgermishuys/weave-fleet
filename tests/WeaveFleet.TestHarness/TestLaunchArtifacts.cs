using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.TestHarness;

/// <summary>
/// Test-harness launch artifacts for <see cref="TestHarness.PrepareRuntimeAsync"/>.
/// Carries no runtime data — the test harness always returns Ready with no-op artifacts.
/// </summary>
internal sealed record TestLaunchArtifacts : RuntimeLaunchArtifacts;
