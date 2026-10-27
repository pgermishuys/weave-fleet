namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// OpenCode 2 live tests about workflows: steps, built-in skills, drafts and automations that run them. See <see cref="OpenCode2LiveTestBase"/>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Harness", "opencode2")]
public sealed partial class OpenCode2WorkflowLiveTests(OpenCode2LiveFleet fleet) : OpenCode2LiveTestBase, IClassFixture<OpenCode2LiveFleet>
{
    protected override OpenCode2LiveFleet Fleet => fleet;
}
