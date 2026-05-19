using FakeLlmServer;
using NuCode.ConformanceTests.Abstractions;
using NuCode.ConformanceTests.NuCode;

namespace NuCode.ConformanceTests.NuCode.Gaps;

/// <summary>
/// GAP: NuCode does not register the <c>task</c> tool, so delegation to child sessions
/// cannot be triggered through the normal agent loop. The <c>TaskTool</c> class exists
/// but is not included in <c>RegisterBuiltInTools</c>.
///
/// Additionally, delegation tracking requires <c>DelegationService</c> and
/// <c>SessionOrchestrator</c> which depend on Fleet DB access via <c>IServiceScopeFactory</c>.
/// The harness test fixture uses <c>NoOpServiceScopeFactory</c>, so even if the task tool
/// were registered, delegation records and child Fleet sessions would not be created.
///
/// These tests document the gap and are expected to FAIL until delegation is fully wired up.
/// </summary>
[Trait("Gap", "delegation")]
public sealed class DelegationGapTests : IAsyncLifetime
{
    private NuCodeFixture _fixture = null!;
    private IHarnessSession _session = null!;
    private string _workDir = null!;

    public async ValueTask InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"gap-delegation-{Guid.NewGuid():N}");
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
    public async Task TaskToolCall_CreatesDelegation()
    {
        // Arrange: script the LLM to call the "task" tool, then respond with text
        var taskToolCall = new ScriptedToolCall(
            Id: "call_task_001",
            Name: "task",
            InputJson: """{"description":"Explore code","prompt":"List all files","subagentType":"explore"}""");

        _fixture.EnqueueResponse(new ScriptedLlmResponse
        {
            ToolCalls = [taskToolCall],
        });

        // Enqueue a follow-up text response for after the tool call
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Done delegating." });

        await _session.SendPromptAsync("Delegate a task", null, CancellationToken.None);

        // Assert: messages should contain evidence of delegation (tool use + tool result)
        var page = await _session.GetMessagesAsync(null, CancellationToken.None);
        var assistantMessages = page.Messages.Where(m => m.Role == "assistant").ToList();

        // GAP: The task tool is not registered in NuCode's built-in tools.
        // The LLM's tool call will not be executed — the agent framework has no "task" function.
        // This means no delegation occurs and no tool result is produced.
        // This assertion is expected to FAIL.
        assistantMessages
            .SelectMany(m => m.Parts)
            .OfType<WeaveFleet.Domain.Harnesses.ToolUsePart>()
            .ShouldContain(
                tp => tp.ToolName == "task",
                "NuCode does not register the task tool. Delegation via tool calls is not supported.");
    }

    [Fact]
    public async Task ChildSession_EventsRouteToCorrectFleetSession()
    {
        // Arrange: script the LLM to call the "task" tool
        var taskToolCall = new ScriptedToolCall(
            Id: "call_task_002",
            Name: "task",
            InputJson: """{"description":"Build project","prompt":"Run dotnet build","subagentType":"build"}""");

        _fixture.EnqueueResponse(new ScriptedLlmResponse
        {
            ToolCalls = [taskToolCall],
        });
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Build complete." });

        var eventsTask = EventCollector.CollectAsync(
            _session,
            evts => evts.Any(e => e.Type == "session.idle"),
            TimeSpan.FromSeconds(10));

        await _session.SendPromptAsync("Build the project", null, CancellationToken.None);
        var events = await eventsTask;

        // GAP: No delegation occurs because the task tool is not registered.
        // Even if it were, child session events would not route correctly because
        // NoOpServiceScopeFactory cannot resolve DelegationService or SessionOrchestrator.
        // This assertion is expected to FAIL.
        events.ShouldContain(
            e => e.Type == "delegation.created",
            "NuCode does not emit delegation.created events. The task tool is not registered and " +
            "DelegationService requires Fleet DB access not available in test fixtures.");
    }
}
