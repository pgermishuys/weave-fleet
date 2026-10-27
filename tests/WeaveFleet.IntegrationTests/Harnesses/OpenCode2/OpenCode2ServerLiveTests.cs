namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// OpenCode 2 live tests about the servers Fleet runs: profiles, the catalog and sign-in. See <see cref="OpenCode2LiveTestBase"/>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Harness", "opencode2")]
public sealed partial class OpenCode2ServerLiveTests(OpenCode2LiveFleet fleet) : OpenCode2LiveTestBase, IClassFixture<OpenCode2LiveFleet>
{
    protected override OpenCode2LiveFleet Fleet => fleet;
}
