namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// OpenCode 2 live tests of what Fleet makes of a session's work, ported from OpenCode 1's live tests, which were the
/// only cover for them: progress from the plan files a model writes, <c>fleet_app_start</c>, the Fleet API skill, and a
/// compacted session's history. See <see cref="OpenCode2LiveTestBase"/>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Harness", "opencode2")]
public sealed partial class OpenCode2SessionLiveTests(OpenCode2LiveFleet fleet) : OpenCode2LiveTestBase, IClassFixture<OpenCode2LiveFleet>
{
    protected override OpenCode2LiveFleet Fleet => fleet;
}
