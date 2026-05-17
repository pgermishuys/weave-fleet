using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NuCode.Agents;
using NuCode.Events;
using NuCode.Fakes;
using NuCode.Sessions;
using NuCode.Tools;

namespace NuCode;

public sealed class TaskToolTests : IDisposable
{
    private readonly SqliteSessionStore _store;
    private readonly NuCodeEventBus _eventBus;
    private readonly SessionService _sessionService;
    private readonly FakeAgentProfileRegistry _profileRegistry = new();
    private readonly FakeNuCodeAgentFactory _agentFactory;
    private readonly FakeToolRegistry _toolRegistry = new();
    private readonly FakeSessionProcessor _processor = new();
    private readonly FakeCompactionService _compactionService = new();
    private readonly FakeChatClient _chatClient = new();
    private readonly TaskTool _sut;

    private readonly AgentProfile _exploreProfile = new()
    {
        Name = "explore",
        Description = "Fast codebase exploration agent",
        Mode = AgentMode.SubAgent,
    };

    private readonly AgentProfile _buildProfile = new()
    {
        Name = "build",
        Description = "Primary build agent",
        Mode = AgentMode.Primary,
    };

    public TaskToolTests()
    {
        _store = new SqliteSessionStore("Data Source=:memory:");
        _eventBus = new NuCodeEventBus();
        _sessionService = new SessionService(_store, _eventBus);
        _agentFactory = new FakeNuCodeAgentFactory(_chatClient);

        _sut = new TaskTool(
            _sessionService,
            _profileRegistry,
            _agentFactory,
            _toolRegistry,
            _processor,
            _compactionService,
            _chatClient,
            NullLogger<TaskTool>.Instance);
    }

    public void Dispose() => _store.Dispose();

    // ── Basic properties ──

    [Fact]
    public void NameIsTask()
    {
        _sut.Name.ShouldBe("task");
    }

    [Fact]
    public void ToAIFunctionReturnsFunction()
    {
        _profileRegistry.Add(_exploreProfile);
        var fn = _sut.ToAIFunction();
        fn.ShouldNotBeNull();
        fn.Name.ShouldBe("task");
    }

    // ── Validation ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task EmptyDescriptionReturnsError(string? description)
    {
        var fn = CreateFunction();
        var result = await InvokeAsync(fn, description: description!, prompt: "do stuff", subagentType: "explore");
        result.ShouldContain("description is required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task EmptyPromptReturnsError(string? prompt)
    {
        var fn = CreateFunction();
        var result = await InvokeAsync(fn, description: "test task", prompt: prompt!, subagentType: "explore");
        result.ShouldContain("prompt is required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task EmptySubagentTypeReturnsError(string? subagentType)
    {
        var fn = CreateFunction();
        var result = await InvokeAsync(fn, description: "test task", prompt: "do stuff", subagentType: subagentType!);
        result.ShouldContain("subagentType is required");
    }

    // ── Agent resolution ──

    [Fact]
    public async Task UnknownAgentTypeReturnsError()
    {
        _profileRegistry.Add(_exploreProfile);

        var fn = CreateFunction();
        var result = await InvokeAsync(fn, description: "test", prompt: "do stuff", subagentType: "nonexistent");

        result.ShouldContain("Unknown agent type 'nonexistent'");
        result.ShouldContain("explore");
    }

    [Fact]
    public async Task PrimaryAgentRejectedAsSubAgent()
    {
        _profileRegistry.Add(_buildProfile);

        var fn = CreateFunction();
        var result = await InvokeAsync(fn, description: "test", prompt: "do stuff", subagentType: "build");

        result.ShouldContain("primary agent");
        result.ShouldContain("cannot be used as a sub-agent");
    }

    // ── Session creation ──

    [Fact]
    public async Task CreatesChildSessionWithParentIdWhenSessionContextIsSet()
    {
        SetupSuccessfulExecution();

        // Simulate being called from within a parent session
        var parentSession = await _sessionService.CreateSessionAsync("/workspace", "Parent", CancellationToken.None);
        SessionContext.Set(parentSession.Id);
        try
        {
            var fn = CreateFunction();
            var result = await InvokeAsync(fn, description: "explore code", prompt: "find files", subagentType: "explore");

            result.ShouldContain("task_id:");
            result.ShouldContain("<task_result>");

            // Verify child session was created with parent ID
            var sessions = await _sessionService.ListSessionsAsync(
                new SessionFilter(), CancellationToken.None);
            var childSession = sessions.FirstOrDefault(s => s.ParentId == parentSession.Id);
            childSession.ShouldNotBeNull();
            childSession.Title.ShouldContain("@explore subagent");
        }
        finally
        {
            SessionContext.Clear();
        }
    }

    [Fact]
    public async Task CreatesRootSessionWhenNoSessionContextIsSet()
    {
        SetupSuccessfulExecution();

        // No SessionContext.Set — simulates being called without a parent
        var fn = CreateFunction();
        var result = await InvokeAsync(fn, description: "explore code", prompt: "find files", subagentType: "explore");

        result.ShouldContain("task_id:");

        // Verify session was created without parent ID
        var sessions = await _sessionService.ListSessionsAsync(
            new SessionFilter(), CancellationToken.None);
        var createdSession = sessions.First(s => s.Title.Contains("@explore subagent"));
        createdSession.ParentId.ShouldBeNull();
    }

    // ── Resume ──

    [Fact]
    public async Task ResumesExistingSessionWhenTaskIdProvided()
    {
        SetupSuccessfulExecution();

        // Create a session to resume
        var existingSession = await _sessionService.CreateSessionAsync(
            ".", "Previous task (@explore subagent)", CancellationToken.None);

        var fn = CreateFunction();
        var result = await InvokeAsync(fn,
            description: "continue",
            prompt: "keep going",
            subagentType: "explore",
            taskId: existingSession.Id.Value);

        result.ShouldContain($"task_id: {existingSession.Id}");
    }

    [Fact]
    public async Task CreatesNewSessionWhenTaskIdNotFound()
    {
        SetupSuccessfulExecution();

        var fn = CreateFunction();
        var result = await InvokeAsync(fn,
            description: "explore code",
            prompt: "find files",
            subagentType: "explore",
            taskId: "nonexistent-id");

        // Should still succeed — falls through to creating new session
        result.ShouldContain("task_id:");
        result.ShouldContain("<task_result>");
    }

    // ── Tool exclusion ──

    [Fact]
    public async Task ExcludesTaskToolFromSubAgentTools()
    {
        _profileRegistry.Add(_exploreProfile);

        var taskTool = new FakeNuCodeTool("task", "task", "task result");
        var readTool = new FakeNuCodeTool("read", "read", "read result");
        _toolRegistry.SetProfileTools([taskTool, readTool]);
        _processor.SetResult(ProcessResult.Stop);

        var fn = CreateFunction();
        await InvokeAsync(fn, description: "test", prompt: "find stuff", subagentType: "explore");

        _agentFactory.CapturedTools.ShouldNotBeNull();
        // Should only have 'read', not 'task'
        _agentFactory.CapturedTools.ShouldHaveSingleItem();
        ((AIFunction)_agentFactory.CapturedTools[0]).Name.ShouldBe("read");
    }

    // ── Successful execution ──

    [Fact]
    public async Task SuccessfulExecutionReturnsTaskResult()
    {
        SetupSuccessfulExecution("The answer is 42.");

        var fn = CreateFunction();
        var result = await InvokeAsync(fn,
            description: "answer question",
            prompt: "what is the meaning of life?",
            subagentType: "explore");

        result.ShouldContain("task_id:");
        result.ShouldContain("<task_result>");
        result.ShouldContain("The answer is 42.");
        result.ShouldContain("</task_result>");
    }

    [Fact]
    public async Task StoresUserMessageAndTextPartInChildSession()
    {
        SetupSuccessfulExecution();

        var fn = CreateFunction();
        var result = await InvokeAsync(fn,
            description: "explore code",
            prompt: "find all controllers",
            subagentType: "explore");

        // Extract session ID from result
        var taskIdLine = result.Split('\n').First(l => l.StartsWith("task_id:"));
        var sessionIdStr = taskIdLine.Replace("task_id:", "").Trim().Split(' ')[0];
        var sessionId = new SessionId(sessionIdStr);

        var messages = await _sessionService.GetMessagesAsync(sessionId, CancellationToken.None);

        // Should have user message + assistant message
        (messages.Count >= 2).ShouldBeTrue();

        var userMwp = messages.First(m => m.Message is UserMessage);
        var userMsg = (UserMessage)userMwp.Message;
        userMsg.Agent.ShouldBe("explore");

        // User message should have a text part with the prompt
        var textPart = userMwp.Parts.OfType<TextPart>().FirstOrDefault();
        textPart.ShouldNotBeNull();
        textPart.Text.ShouldBe("find all controllers");
    }

    // ── Description includes sub-agents ──

    [Fact]
    public void DescriptionListsAvailableSubAgents()
    {
        _profileRegistry.Add(_buildProfile);
        _profileRegistry.Add(_exploreProfile);

        var description = _sut.Description;

        description.ShouldContain("explore");
        description.ShouldContain("Fast codebase exploration agent");
        // Should not list primary agents
        description.ShouldNotContain("- build:");
    }

    // ── Compaction ──

    [Fact]
    public async Task ProcessResult_Compact_triggers_compaction_and_retry()
    {
        _profileRegistry.Add(_exploreProfile);
        _toolRegistry.SetProfileTools([new FakeNuCodeTool("read", "read", "read result")]);

        var callCount = 0;
        _processor.OnProcess(async (agent, assistantMsg, chatMessages, session, ct) =>
        {
            callCount++;
            if (callCount == 1)
            {
                return ProcessResult.Compact;
            }

            var textPart = new TextPart(
                PartId.New(), assistantMsg.SessionId, assistantMsg.Id, "retried response");
            await _sessionService.UpsertPartAsync(textPart, ct);
            return ProcessResult.Stop;
        });

        var fn = CreateFunction();
        var result = await InvokeAsync(fn, description: "test", prompt: "do stuff", subagentType: "explore");

        _compactionService.CompactCallCount.ShouldBe(1);
        _compactionService.LastOverflow.ShouldBe(true);
        callCount.ShouldBe(2);
        result.ShouldContain("retried response");
    }

    [Fact]
    public async Task ProcessResult_Compact_on_retry_stops_without_infinite_loop()
    {
        _profileRegistry.Add(_exploreProfile);
        _toolRegistry.SetProfileTools([new FakeNuCodeTool("read", "read", "read result")]);

        // Both calls return Compact — second should not trigger another compaction
        _processor.SetResult(ProcessResult.Compact);

        var fn = CreateFunction();
        var result = await InvokeAsync(fn, description: "test", prompt: "do stuff", subagentType: "explore");

        // Only one compaction (from the first Compact result)
        _compactionService.CompactCallCount.ShouldBe(1);
        result.ShouldContain("task_id:");
    }

    [Fact]
    public async Task ProcessResult_Continue_with_NeedsCompaction_triggers_proactive_compaction()
    {
        _profileRegistry.Add(_exploreProfile);
        _toolRegistry.SetProfileTools([new FakeNuCodeTool("read", "read", "read result")]);

        _compactionService.SetNeedsCompaction(true);

        var callCount = 0;
        _processor.OnProcess(async (agent, assistantMsg, chatMessages, session, ct) =>
        {
            callCount++;
            if (callCount == 1)
            {
                return ProcessResult.Continue;
            }

            // After proactive compaction, stop
            var textPart = new TextPart(
                PartId.New(), assistantMsg.SessionId, assistantMsg.Id, "after compaction");
            await _sessionService.UpsertPartAsync(textPart, ct);
            return ProcessResult.Stop;
        });

        var fn = CreateFunction();
        var result = await InvokeAsync(fn, description: "test", prompt: "do stuff", subagentType: "explore");

        _compactionService.CompactCallCount.ShouldBe(1);
        _compactionService.LastOverflow.ShouldBe(false);
        result.ShouldContain("after compaction");
    }

    // ── Helpers ──

    private AIFunction CreateFunction()
    {
        _profileRegistry.Add(_exploreProfile);
        _profileRegistry.Add(_buildProfile);
        return _sut.ToAIFunction();
    }

    private static async Task<string> InvokeAsync(
        AIFunction fn,
        string description,
        string prompt,
        string subagentType,
        string? taskId = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["description"] = description,
            ["prompt"] = prompt,
            ["subagentType"] = subagentType,
        };
        if (taskId is not null)
        {
            args["taskId"] = taskId;
        }

        var result = await fn.InvokeAsync(new AIFunctionArguments(args), CancellationToken.None);
        return result?.ToString() ?? "";
    }

    private void SetupSuccessfulExecution(string responseText = "sub-agent response")
    {
        _profileRegistry.Add(_exploreProfile);
        _toolRegistry.SetProfileTools([new FakeNuCodeTool("read", "read", "read result")]);
        _processor.OnProcess(async (agent, assistantMsg, chatMessages, session, ct) =>
        {
            var textPart = new TextPart(
                PartId.New(),
                assistantMsg.SessionId,
                assistantMsg.Id,
                responseText);
            await _sessionService.UpsertPartAsync(textPart, ct);
            return ProcessResult.Stop;
        });
    }

    private ChatClientAgent CreateDummyAgent()
    {
        return _chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "test-agent",
        });
    }
}
