using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NuCode.Agents;
using NuCode.Audit;
using NuCode.Events;
using NuCode.Fakes;
using NuCode.Mcp;
using NuCode.Plugins;
using NuCode.Sessions;
using NuCode.Tools;

namespace NuCode.Integration;

/// <summary>
/// End-to-end integration tests that wire up NuCode via DI and exercise
/// the full pipeline: DI → session → agent → stream processing → events.
/// Uses a mock <see cref="IChatClient"/> that returns synthetic responses
/// so no real LLM is needed.
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public EndToEndTests()
    {
        var services = new ServiceCollection();
        services.AddNuCode(options =>
        {
            options.WorkingDirectory = Path.GetTempPath();
        });
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _provider.DisposeAsync();
    }

    // ── Helpers ──

    private ISessionService SessionService => _scope.ServiceProvider.GetRequiredService<ISessionService>();
    private INuCodeEventBus EventBus => _scope.ServiceProvider.GetRequiredService<INuCodeEventBus>();
    private INuCodeAgentFactory AgentFactory => _provider.GetRequiredService<INuCodeAgentFactory>();
    private IAgentProfileRegistry ProfileRegistry => _provider.GetRequiredService<IAgentProfileRegistry>();
    private IToolRegistry ToolRegistry => _provider.GetRequiredService<IToolRegistry>();
    private IPluginRegistry PluginRegistry => _provider.GetRequiredService<IPluginRegistry>();
    private IMcpManager McpManager => _provider.GetRequiredService<IMcpManager>();

    private SessionProcessor CreateProcessor() =>
        new(SessionService,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionProcessor>.Instance,
            new AuditEventSubscriber(new NullAuditService(), EventBus));

    private static AgentResponseUpdate TextUpdate(string text) =>
        new() { Contents = [new TextContent(text)] };

    private static AgentResponseUpdate FinishUpdate(ChatFinishReason reason) =>
        new() { FinishReason = reason };

    private static AgentResponseUpdate FunctionCallUpdate(string callId, string name, IDictionary<string, object?>? args = null) =>
        new() { Contents = [new FunctionCallContent(callId, name, args)] };

    private static AgentResponseUpdate FunctionResultUpdate(string callId, object? result = null) =>
        new() { Contents = [new FunctionResultContent(callId, result)] };

    private static async IAsyncEnumerable<AgentResponseUpdate> ToStream(params AgentResponseUpdate[] updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

    // ── Tests ──

    [Fact]
    public void AllCoreServicesResolveFromDi()
    {
        SessionService.ShouldNotBeNull();
        EventBus.ShouldNotBeNull();
        AgentFactory.ShouldNotBeNull();
        ProfileRegistry.ShouldNotBeNull();
        ToolRegistry.ShouldNotBeNull();
        PluginRegistry.ShouldNotBeNull();
        McpManager.ShouldNotBeNull();
    }

    [Fact]
    public void BuildProfileExistsInRegistry()
    {
        var build = ProfileRegistry.Get("build");
        build.ShouldNotBeNull();
        build.Mode.ShouldBe(AgentMode.Primary);
    }

    [Fact]
    public async Task FullSessionLifecycleWithTextResponse()
    {
        // 1. Create session
        var session = await SessionService.CreateSessionAsync(
            Path.GetTempPath(), "E2E Text Test", CancellationToken.None);
        session.ShouldNotBeNull();
        session.Title.ShouldBe("E2E Text Test");

        // 2. Create user message
        var userMsg = new UserMessage(
            MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await SessionService.UpsertMessageAsync(userMsg, CancellationToken.None);

        // 3. Create assistant message
        var assistantMsg = new AssistantMessage(
            MessageId.New(), session.Id, DateTimeOffset.UtcNow,
            ParentId: userMsg.Id, Agent: "build",
            ProviderId: "test", ModelId: "test-model");
        await SessionService.UpsertMessageAsync(assistantMsg, CancellationToken.None);

        // 4. Process a text-only stream
        var processor = CreateProcessor();
        var agentSession = new NuCodeAgentSession(session);
        var stream = ToStream(
            TextUpdate("Hello, "),
            TextUpdate("world!"),
            FinishUpdate(ChatFinishReason.Stop));

        var result = await processor.ProcessStreamAsync(stream, assistantMsg, agentSession, CancellationToken.None);
        result.ShouldBe(ProcessResult.Stop);

        // 5. Verify messages and parts were persisted
        var messages = await SessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Count.ShouldBe(2); // User + Assistant

        var assistantWithParts = messages.OfType<MessageWithParts>()
            .First(m => m.Message is AssistantMessage);
        assistantWithParts.Parts.ShouldHaveSingleItem();

        var textPart = assistantWithParts.Parts[0].ShouldBeOfType<TextPart>();
        textPart.Text.ShouldBe("Hello, world!");
    }

    [Fact]
    public async Task FullSessionLifecycleWithToolCall()
    {
        // 1. Create session
        var session = await SessionService.CreateSessionAsync(
            Path.GetTempPath(), "E2E Tool Test", CancellationToken.None);

        // 2. Create messages
        var userMsg = new UserMessage(
            MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await SessionService.UpsertMessageAsync(userMsg, CancellationToken.None);

        var assistantMsg = new AssistantMessage(
            MessageId.New(), session.Id, DateTimeOffset.UtcNow,
            ParentId: userMsg.Id, Agent: "build",
            ProviderId: "test", ModelId: "test-model");
        await SessionService.UpsertMessageAsync(assistantMsg, CancellationToken.None);

        // 3. Process a stream with a tool call (call + result + finish)
        var processor = CreateProcessor();
        var agentSession = new NuCodeAgentSession(session);
        var stream = ToStream(
            FunctionCallUpdate("call-1", "read", new Dictionary<string, object?> { ["file_path"] = "/tmp/test.txt" }),
            FunctionResultUpdate("call-1", "file contents here"),
            FinishUpdate(ChatFinishReason.ToolCalls));

        var result = await processor.ProcessStreamAsync(stream, assistantMsg, agentSession, CancellationToken.None);
        result.ShouldBe(ProcessResult.Continue);

        // 4. Verify tool part was created with correct states
        var messages = await SessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        var assistantWithParts = messages.OfType<MessageWithParts>()
            .First(m => m.Message is AssistantMessage);

        var toolPart = assistantWithParts.Parts.OfType<ToolPart>().Single();
        toolPart.ToolName.ShouldBe("read");
        toolPart.CallId.ShouldBe("call-1");
        toolPart.State.ShouldBeOfType<CompletedToolCallState>();
    }

    [Fact]
    public async Task EventBusFiresSessionCreatedEvent()
    {
        NuCodeEvent<SessionEvents.SessionInfo>? receivedEvent = null;
        EventBus.Subscribe(SessionEvents.Created, e => receivedEvent = e);

        var session = await SessionService.CreateSessionAsync(
            Path.GetTempPath(), "Event Test", CancellationToken.None);

        receivedEvent.ShouldNotBeNull();
        receivedEvent.Properties.SessionId.ShouldBe(session.Id);
    }

    [Fact]
    public async Task EventBusFiresMessageAndPartEvents()
    {
        var partUpdatedEvents = new List<NuCodeEvent<MessageEvents.PartInfo>>();
        EventBus.Subscribe(MessageEvents.PartUpdated, e => partUpdatedEvents.Add(e));

        // Create session + messages
        var session = await SessionService.CreateSessionAsync(
            Path.GetTempPath(), "Part Event Test", CancellationToken.None);

        var userMsg = new UserMessage(
            MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
        await SessionService.UpsertMessageAsync(userMsg, CancellationToken.None);

        var assistantMsg = new AssistantMessage(
            MessageId.New(), session.Id, DateTimeOffset.UtcNow,
            ParentId: userMsg.Id, Agent: "build",
            ProviderId: "test", ModelId: "test-model");
        await SessionService.UpsertMessageAsync(assistantMsg, CancellationToken.None);

        // Process text stream → should create text part → fire part.updated
        var processor = CreateProcessor();
        var agentSession = new NuCodeAgentSession(session);
        var stream = ToStream(
            TextUpdate("Some output"),
            FinishUpdate(ChatFinishReason.Stop));

        await processor.ProcessStreamAsync(stream, assistantMsg, agentSession, CancellationToken.None);

        // At least one part.updated event for the text part
        partUpdatedEvents.ShouldNotBeEmpty();
        partUpdatedEvents.ShouldContain(e => e.Properties.SessionId == session.Id.Value);
    }

    [Fact]
    public async Task PluginHooksCanModifySystemPromptDuringLifecycle()
    {
        // Register a plugin that appends to system prompts
        PluginRegistry.Register(new SystemPromptPlugin());

        // Trigger the hook
        var output = await PluginRegistry.TriggerAsync(
            BuiltInHooks.SystemPromptTransform,
            new SystemPromptInput { SessionId = "test-session", Model = "gpt-4" },
            new SystemPromptOutput { Segments = ["Base prompt."] });

        output.Segments.ShouldBe(["Base prompt.", "Plugin: Always be helpful."]);
    }

    [Fact]
    public async Task SessionArchiveAndRetrieve()
    {
        // Create, archive, then verify
        var session = await SessionService.CreateSessionAsync(
            Path.GetTempPath(), "Archive Test", CancellationToken.None);
        session.ArchivedAt.ShouldBeNull();

        var archived = await SessionService.ArchiveSessionAsync(session.Id, CancellationToken.None);
        archived.ArchivedAt.ShouldNotBeNull();

        // Unarchive
        var unarchived = await SessionService.UnarchiveSessionAsync(session.Id, CancellationToken.None);
        unarchived.ArchivedAt.ShouldBeNull();
    }

    [Fact]
    public void AgentFactoryCreatesAgentFromBuildProfile()
    {
        var profile = ProfileRegistry.Get("build")!;
        var mockClient = new FakeMinimalChatClient();
        var tools = ToolRegistry.GetAll().Select(t => (AITool)t.ToAIFunction()).ToList();

        var agent = AgentFactory.CreateAgent(profile, mockClient, tools);

        agent.ShouldNotBeNull();
    }

    [Fact]
    public async Task MultiStepConversationAccumulatesMessages()
    {
        var session = await SessionService.CreateSessionAsync(
            Path.GetTempPath(), "Multi-Step Test", CancellationToken.None);

        // Simulate 3 turns: user → assistant, user → assistant, user → assistant
        for (var turn = 0; turn < 3; turn++)
        {
            var userMsg = new UserMessage(
                MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build");
            await SessionService.UpsertMessageAsync(userMsg, CancellationToken.None);

            var assistantMsg = new AssistantMessage(
                MessageId.New(), session.Id, DateTimeOffset.UtcNow,
                ParentId: userMsg.Id, Agent: "build",
                ProviderId: "test", ModelId: "test-model");
            await SessionService.UpsertMessageAsync(assistantMsg, CancellationToken.None);

            var processor = CreateProcessor();
            var agentSession = new NuCodeAgentSession(session);
            await processor.ProcessStreamAsync(
                ToStream(TextUpdate($"Response {turn}"), FinishUpdate(ChatFinishReason.Stop)),
                assistantMsg, agentSession, CancellationToken.None);
        }

        var messages = await SessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Count.ShouldBe(6); // 3 user + 3 assistant
    }

    // ── Test helpers ──

    /// <summary>
    /// A simple test plugin that appends a segment to system prompts.
    /// </summary>
    private sealed class SystemPromptPlugin : INuCodePlugin
    {
        public string Name => "system-prompt-test";

        public NuCodeHookCollection Initialize(IServiceProvider services) =>
            new NuCodeHookCollection()
                .On(BuiltInHooks.SystemPromptTransform, (_, output) =>
                {
                    output.Segments.Add("Plugin: Always be helpful.");
                    return Task.CompletedTask;
                });
    }

    /// <summary>
    /// Minimal fake IChatClient for agent creation tests.
    /// Does not need to implement actual completion — just satisfies the interface for factory.
    /// </summary>
    private sealed class FakeMinimalChatClient : IChatClient
    {
        public void Dispose() { }

        public ChatClientMetadata Metadata { get; } = new();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "test")]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            EmptyStream();

        private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
