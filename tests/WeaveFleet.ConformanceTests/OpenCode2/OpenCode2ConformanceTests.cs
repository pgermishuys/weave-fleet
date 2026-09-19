using WeaveFleet.ConformanceTests.Abstractions;

namespace WeaveFleet.ConformanceTests.OpenCode2;

/// <summary>
/// Runs the shared <see cref="HarnessConformanceBase"/> tests against <see cref="OpenCode2Fixture"/>: a real
/// <c>opencode2</c> with a scratch HOME and the scripted model, one server for the class. Skipped when
/// <c>opencode2</c> isn't installed, unless <c>FLEET_REQUIRE_OPENCODE2=1</c> (CI), which fails them instead.
/// </summary>
[Trait("Harness", "opencode2")]
public sealed class OpenCode2ConformanceTests(OpenCode2Host host) : HarnessConformanceBase, IClassFixture<OpenCode2Host>
{
    private const string NotInstalled = "OpenCode 2 (opencode2, 2.x) isn't installed where the harness looks: ~/.weave/harnesses/opencode2, PATH or ~/.opencode/bin.";

    public override async ValueTask InitializeAsync()
    {
        if (!OpenCode2Host.IsAvailable())
        {
            if (Environment.GetEnvironmentVariable("FLEET_REQUIRE_OPENCODE2") == "1")
                throw new InvalidOperationException(NotInstalled + " FLEET_REQUIRE_OPENCODE2=1 fails the test instead of skipping it.");
            throw new InvalidOperationException(Xunit.v3.DynamicSkipToken.Value + NotInstalled);
        }

        await base.InitializeAsync();
    }

    protected override IHarnessSessionFixture CreateFixture() => new OpenCode2Fixture(host);

    /// <inheritdoc />
    protected override IReadOnlyDictionary<string, string> NotApplicable { get; } = new Dictionary<string, string>
    {
        [nameof(ResumeToken_IsPopulated_AfterFirstPrompt)] =
            "OpenCode 2 creates the session when Fleet starts it, so the resume token is there before the first prompt.",
        [nameof(SubscribeAsync_EmitsMessageCreated_ForUserMessage)] =
            "OpenCode 2 streams the assistant's messages only; Fleet shows the prompt itself, and history has it.",
        [nameof(SubscribeAsync_EmitsSessionCreated_OnFirstPrompt)] =
            "OpenCode 2 creates the session when Fleet starts it, before any prompt, so a prompt creates nothing.",
        [nameof(SubscribeAsync_EmitsSessionCreatedAndBusy_OnFirstPrompt)] =
            "OpenCode 2 creates the session when Fleet starts it, before any prompt, so a prompt creates nothing.",
    };
}
