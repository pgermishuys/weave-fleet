using FakeLlmServer;
using NuCode.ConformanceTests.NuCode;

namespace NuCode.ConformanceTests.NuCode.Gaps;

/// <summary>
/// GAP: NuCode ignores <see cref="PromptOptions.ModelId"/> and <see cref="PromptOptions.ProviderId"/>.
/// The harness is constructed with a fixed provider/model and never switches per-request.
/// These tests document the gap and are expected to FAIL until the feature is implemented.
/// </summary>
[Trait("Gap", "model-selection")]
public sealed class ModelSelectionGapTests : IAsyncLifetime
{
    private NuCodeFixture _fixture = null!;
    private IHarnessSession _session = null!;
    private string _workDir = null!;

    public async ValueTask InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"gap-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _fixture = new NuCodeFixture();
        _session = await _fixture.CreateSessionAsync(_workDir);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _session.DisposeAsync();
        await _fixture.DisposeAsync();
        if (Directory.Exists(_workDir))
            Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public async Task SendPromptAsync_WithDifferentModelId_UsesRequestedModel()
    {
        // Arrange: enqueue a response and send with a specific model
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });
        var options = new PromptOptions { ModelId = "gpt-4o" };

        await _session.SendPromptAsync("Hello", options, CancellationToken.None);

        // Assert: the assistant message should reflect the requested model
        var page = await _session.GetMessagesAsync(null, CancellationToken.None);
        var assistantMsg = page.Messages.FirstOrDefault(m => m.Role == "assistant");
        assistantMsg.ShouldNotBeNull();

        // GAP: NuCode uses the fixed model from construction, not options.ModelId.
        // This assertion is expected to FAIL — NuCode will report "fake-model" not "gpt-4o".
        assistantMsg.ModelId.ShouldBe("gpt-4o");
    }

    [Fact]
    public async Task SendPromptAsync_WithDifferentProviderId_ReflectsRequestedProvider()
    {
        // Arrange: enqueue a response and send with a specific provider
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });
        var options = new PromptOptions { ProviderId = "openai", ModelId = "gpt-4o" };

        await _session.SendPromptAsync("Hello", options, CancellationToken.None);

        // Assert: the providers list should include the requested provider after the prompt
        var providers = await _session.GetProvidersAsync(CancellationToken.None);

        // GAP: NuCode always returns the fixed provider from construction, not options.ProviderId.
        // This assertion is expected to FAIL — NuCode will return "fake" not "openai".
        providers.ShouldContain(p => p.Id == "openai");
    }
}
