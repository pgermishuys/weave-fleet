namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// OpenCode 2 live tests about work that outlives a turn: background shells and subagents, their environment and what Fleet does when they are lost. See <see cref="OpenCode2LiveTestBase"/>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Harness", "opencode2")]
public sealed partial class OpenCode2BackgroundLiveTests(OpenCode2LiveFleet fleet) : OpenCode2LiveTestBase, IClassFixture<OpenCode2LiveFleet>
{
    protected override OpenCode2LiveFleet Fleet => fleet;
}
