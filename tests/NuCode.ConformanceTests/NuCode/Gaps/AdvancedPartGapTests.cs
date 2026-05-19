using FakeLlmServer;
using NuCode.ConformanceTests.NuCode;

namespace NuCode.ConformanceTests.NuCode.Gaps;

/// <summary>
/// GAP: NuCode does not emit advanced message parts that OpenCode produces.
/// Specifically: agent delegation parts, subtask parts, and patch/diff parts
/// are not present in NuCode's message output.
/// These tests document the gap and are expected to FAIL until the feature is implemented.
/// </summary>
[Trait("Gap", "advanced-parts")]
public sealed class AdvancedPartGapTests : IAsyncLifetime
{
    private NuCodeFixture _fixture = null!;
    private IHarnessSession _session = null!;
    private string _workDir = null!;

    public async ValueTask InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"gap-parts-{Guid.NewGuid():N}");
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
    public void AgentPart_IsEmitted_WhenDelegating()
    {
        // GAP: NuCode does not emit agent delegation parts.
        // OpenCode emits a part indicating which sub-agent handled a delegated task.
        // IHarnessSession / MessagePart hierarchy does not yet include an AgentPart type.
        // This test is expected to FAIL — it documents the missing feature.
        true.ShouldBeFalse(
            "NuCode does not emit agent delegation parts. " +
            "The MessagePart hierarchy does not include an AgentPart type.");
    }

    [Fact]
    public void SubtaskPart_IsEmitted_ForChildSessions()
    {
        // GAP: NuCode does not emit subtask parts for child/delegated sessions.
        // OpenCode emits subtask parts that link parent and child session activity.
        // IHarnessSession / MessagePart hierarchy does not yet include a SubtaskPart type.
        // This test is expected to FAIL — it documents the missing feature.
        true.ShouldBeFalse(
            "NuCode does not emit subtask parts for child sessions. " +
            "The MessagePart hierarchy does not include a SubtaskPart type.");
    }

    [Fact]
    public async Task PatchPart_IsEmitted_OnEdit()
    {
        // Arrange: send a prompt that would trigger a file edit
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "I edited the file." });
        await _session.SendPromptAsync("Edit a file", null, CancellationToken.None);

        var page = await _session.GetMessagesAsync(null, CancellationToken.None);
        var assistantMsg = page.Messages.FirstOrDefault(m => m.Role == "assistant");
        assistantMsg.ShouldNotBeNull();

        // GAP: NuCode does not emit patch/diff parts when files are edited.
        // OpenCode emits a patch part showing the diff of changes made.
        // The MessagePart hierarchy does not yet include a PatchPart type.
        // This assertion is expected to FAIL — it documents the missing feature.
        assistantMsg.Parts.ShouldContain(
            p => p.GetType().Name == "PatchPart",
            "NuCode does not emit patch parts for file edits. The MessagePart hierarchy does not include a PatchPart type.");
    }
}
