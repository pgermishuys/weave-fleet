using FakeLlmServer;
using NuCode.ConformanceTests.NuCode;

namespace NuCode.ConformanceTests.NuCode.Gaps;

/// <summary>
/// Tests that <see cref="PromptOptions.ModelId"/> and <see cref="PromptOptions.ProviderId"/>
/// are respected when sending prompts. Previously a gap — NuCode ignored these options and
/// always used the fixed model/provider from construction.
/// </summary>
public sealed class ModelSelectionTests : IAsyncLifetime
{
    private NuCodeFixture _fixture = null!;
    private IHarnessSession _session = null!;
    private string _workDir = null!;

    public async ValueTask InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"model-sel-{Guid.NewGuid():N}");
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
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });
        var options = new PromptOptions { ModelId = "gpt-4o" };

        await _session.SendPromptAsync("Hello", options, CancellationToken.None);

        var page = await _session.GetMessagesAsync(null, CancellationToken.None);
        var assistantMsg = page.Messages.FirstOrDefault(m => m.Role == "assistant");
        assistantMsg.ShouldNotBeNull();
        assistantMsg.ModelId.ShouldBe("gpt-4o");
    }

    [Fact]
    public async Task SendPromptAsync_WithModelIdAndProviderId_RecordsModelOnMessage()
    {
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });
        var options = new PromptOptions { ProviderId = "openai", ModelId = "gpt-4o-mini" };

        await _session.SendPromptAsync("Hello", options, CancellationToken.None);

        var page = await _session.GetMessagesAsync(null, CancellationToken.None);
        var assistantMsg = page.Messages.FirstOrDefault(m => m.Role == "assistant");
        assistantMsg.ShouldNotBeNull();
        assistantMsg.ModelId.ShouldBe("gpt-4o-mini");
    }

    [Fact]
    public async Task SendPromptAsync_WithoutOptions_UsesDefaultModel()
    {
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });

        await _session.SendPromptAsync("Hello", null, CancellationToken.None);

        var page = await _session.GetMessagesAsync(null, CancellationToken.None);
        var assistantMsg = page.Messages.FirstOrDefault(m => m.Role == "assistant");
        assistantMsg.ShouldNotBeNull();
        // NuCodeFixture constructs with modelId: "fake-model"
        assistantMsg.ModelId.ShouldBe("fake-model");
    }
}
