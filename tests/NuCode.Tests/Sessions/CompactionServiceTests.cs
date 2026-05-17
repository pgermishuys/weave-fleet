using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NuCode.Configuration;
using NuCode.Events;
using NuCode.Plugins;
using NuCode.Sessions;

namespace NuCode;

public sealed class CompactionServiceTests : IDisposable
{
    private readonly SqliteSessionStore _store;
    private readonly NuCodeEventBus _eventBus;
    private readonly SessionService _sessionService;
    private readonly PluginRegistry _pluginRegistry;

    public CompactionServiceTests()
    {
        _store = new SqliteSessionStore("Data Source=:memory:");
        _eventBus = new NuCodeEventBus();
        _sessionService = new SessionService(_store, _eventBus);
        _pluginRegistry = new PluginRegistry(new MinimalServiceProvider(), null);
    }

    public void Dispose() => _store.Dispose();

    // ── Helpers ──

    private CompactionService CreateService(
        CompactionConfig? config = null,
        IChatClient? chatClient = null)
    {
        return new CompactionService(
            _sessionService,
            _pluginRegistry,
            chatClient ?? new SummarizingChatClient("This is a summary of the conversation."),
            config,
            NullLogger<CompactionService>.Instance);
    }

    private async Task<NuCodeSession> CreateSessionWithMessagesAsync(int messageCount)
    {
        var session = await _sessionService.CreateSessionAsync("/workspace", "Test", CancellationToken.None);

        for (var i = 0; i < messageCount; i++)
        {
            var isUser = i % 2 == 0;
            NuCodeMessage msg = isUser
                ? new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build")
                : new AssistantMessage(
                    MessageId.New(), session.Id, DateTimeOffset.UtcNow,
                    ParentId: MessageId.New(), Agent: "build",
                    ProviderId: "test", ModelId: "test-model");
            await _sessionService.UpsertMessageAsync(msg, CancellationToken.None);

            var textPart = new TextPart(
                PartId.New(), session.Id, msg.Id,
                $"Message {i} content with some text to make it realistic.");
            await _sessionService.UpsertPartAsync(textPart, CancellationToken.None);
        }

        return session;
    }

    // ── NeedsCompactionAsync ──

    [Fact]
    public async Task NeedsCompaction_BelowThreshold_ReturnsFalse()
    {
        var session = await CreateSessionWithMessagesAsync(5);
        var service = CreateService(new CompactionConfig { Auto = true, MessageThreshold = 10 });

        var result = await service.NeedsCompactionAsync(session.Id, CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task NeedsCompaction_AboveThreshold_ReturnsTrue()
    {
        var session = await CreateSessionWithMessagesAsync(15);
        var service = CreateService(new CompactionConfig { Auto = true, MessageThreshold = 10 });

        var result = await service.NeedsCompactionAsync(session.Id, CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task NeedsCompaction_AutoDisabled_ReturnsFalse()
    {
        var session = await CreateSessionWithMessagesAsync(100);
        var service = CreateService(new CompactionConfig { Auto = false, MessageThreshold = 10 });

        var result = await service.NeedsCompactionAsync(session.Id, CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task NeedsCompaction_TokenThresholdExceeded_ReturnsTrue()
    {
        var session = await CreateSessionWithMessagesAsync(5);
        // Each message has ~50 chars ≈ 12 tokens, 5 messages ≈ 60 tokens
        var service = CreateService(new CompactionConfig { Auto = true, MessageThreshold = 100, TokenThreshold = 10 });

        var result = await service.NeedsCompactionAsync(session.Id, CancellationToken.None);

        result.ShouldBeTrue();
    }

    // ── CompactAsync ──

    [Fact]
    public async Task Compact_ProducesSummaryAndCompactionPart()
    {
        var session = await CreateSessionWithMessagesAsync(20);
        var service = CreateService(new CompactionConfig
        {
            Auto = true,
            Prune = true,
            RecentMessagesToKeep = 5,
        });

        await service.CompactAsync(session.Id, overflow: false, CancellationToken.None);

        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);

        // Should have: 5 kept messages + 1 summary message = 6
        messages.Count.ShouldBe(6);

        // Last message should be the summary (synthetic, inserted after pruning)
        var summaryMsg = messages.Last();
        summaryMsg.Message.Role.ShouldBe(MessageRole.User);
        var textParts = summaryMsg.Parts.OfType<TextPart>().ToList();
        textParts.ShouldHaveSingleItem();
        textParts[0].Text.ShouldContain("Conversation Summary");
        textParts[0].Synthetic.ShouldBeTrue();

        // Should have a CompactionPart
        var compactionParts = summaryMsg.Parts.OfType<CompactionPart>().ToList();
        compactionParts.ShouldHaveSingleItem();
        compactionParts[0].Overflow.ShouldBeFalse();
    }

    [Fact]
    public async Task Compact_OverflowFlag_SetOnCompactionPart()
    {
        var session = await CreateSessionWithMessagesAsync(20);
        var service = CreateService(new CompactionConfig
        {
            Auto = true,
            Prune = true,
            RecentMessagesToKeep = 5,
        });

        await service.CompactAsync(session.Id, overflow: true, CancellationToken.None);

        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        var compactionPart = messages.SelectMany(m => m.Parts).OfType<CompactionPart>().ShouldHaveSingleItem();
        compactionPart.Overflow.ShouldBeTrue();
    }

    [Fact]
    public async Task Compact_PreservesRecentMessages()
    {
        var session = await CreateSessionWithMessagesAsync(20);
        var service = CreateService(new CompactionConfig
        {
            Auto = true,
            Prune = true,
            RecentMessagesToKeep = 10,
        });

        await service.CompactAsync(session.Id, overflow: false, CancellationToken.None);

        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);

        // 10 kept + 1 summary = 11
        messages.Count.ShouldBe(11);
    }

    [Fact]
    public async Task Compact_CompactingAtIsSetAndCleared()
    {
        var session = await CreateSessionWithMessagesAsync(20);
        var service = CreateService(new CompactionConfig
        {
            Auto = true,
            Prune = true,
            RecentMessagesToKeep = 5,
        });

        await service.CompactAsync(session.Id, overflow: false, CancellationToken.None);

        // After compaction, CompactingAt should be cleared
        var updatedSession = await _sessionService.GetSessionAsync(session.Id, CancellationToken.None);
        updatedSession.ShouldNotBeNull();
        updatedSession!.CompactingAt.ShouldBeNull();
    }

    [Fact]
    public async Task Compact_HookCancelsCompaction()
    {
        var session = await CreateSessionWithMessagesAsync(20);

        // Register a plugin that cancels compaction
        _pluginRegistry.Register(new CancellingCompactionPlugin());

        var service = CreateService(new CompactionConfig
        {
            Auto = true,
            Prune = true,
            RecentMessagesToKeep = 5,
        });

        await service.CompactAsync(session.Id, overflow: false, CancellationToken.None);

        // All 20 original messages should still be there (no compaction happened)
        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Count.ShouldBe(20);
    }

    [Fact]
    public async Task Compact_EmptySession_NoOp()
    {
        var session = await _sessionService.CreateSessionAsync("/workspace", "Empty", CancellationToken.None);
        var service = CreateService(new CompactionConfig { Auto = true, Prune = true, RecentMessagesToKeep = 5 });

        // Should not throw
        await service.CompactAsync(session.Id, overflow: false, CancellationToken.None);

        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Compact_TooFewMessages_NoOp()
    {
        var session = await CreateSessionWithMessagesAsync(3);
        var service = CreateService(new CompactionConfig { Auto = true, Prune = true, RecentMessagesToKeep = 10 });

        await service.CompactAsync(session.Id, overflow: false, CancellationToken.None);

        // All 3 messages still there — not enough to compact
        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Compact_LLMFailure_ClearsCompactingAt()
    {
        var session = await CreateSessionWithMessagesAsync(20);
        var service = CreateService(
            new CompactionConfig { Auto = true, Prune = true, RecentMessagesToKeep = 5 },
            new ThrowingChatClient());

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => service.CompactAsync(session.Id, overflow: false, CancellationToken.None));
        ex.Message.ShouldContain("LLM failure");

        // CompactingAt must be cleared despite the failure
        var updatedSession = await _sessionService.GetSessionAsync(session.Id, CancellationToken.None);
        updatedSession.ShouldNotBeNull();
        updatedSession!.CompactingAt.ShouldBeNull();
    }

    [Fact]
    public async Task Compact_ConcurrentCompaction_SkipsSecondCall()
    {
        var session = await CreateSessionWithMessagesAsync(20);
        var service = CreateService(new CompactionConfig
        {
            Auto = true,
            Prune = true,
            RecentMessagesToKeep = 5,
        });

        // Simulate compaction already in progress by setting CompactingAt
        await _sessionService.SetCompactingAsync(session.Id, CancellationToken.None);

        await service.CompactAsync(session.Id, overflow: false, CancellationToken.None);

        // All 20 messages should still be there — second compaction was skipped
        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        messages.Count.ShouldBe(20);
    }

    [Fact]
    public async Task Compact_NonPruneMode_MarksToolPartsWithCompactedTime()
    {
        // Create session with a tool part
        var session = await _sessionService.CreateSessionAsync("/workspace", "Test", CancellationToken.None);

        for (var i = 0; i < 20; i++)
        {
            var isUser = i % 2 == 0;
            NuCodeMessage msg = isUser
                ? new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build")
                : new AssistantMessage(
                    MessageId.New(), session.Id, DateTimeOffset.UtcNow,
                    ParentId: MessageId.New(), Agent: "build",
                    ProviderId: "test", ModelId: "test-model");
            await _sessionService.UpsertMessageAsync(msg, CancellationToken.None);

            var textPart = new TextPart(PartId.New(), session.Id, msg.Id, $"Message {i}");
            await _sessionService.UpsertPartAsync(textPart, CancellationToken.None);

            // Add a tool part to assistant messages
            if (!isUser)
            {
                var toolPart = new ToolPart(
                    PartId.New(), session.Id, msg.Id, $"call-{i}", "read",
                    new CompletedToolCallState(
                        System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty,
                        "tool output", "Read file", System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                await _sessionService.UpsertPartAsync(toolPart, CancellationToken.None);
            }
        }

        var service = CreateService(new CompactionConfig
        {
            Auto = true,
            Prune = false, // Non-prune mode
            RecentMessagesToKeep = 5,
        });

        await service.CompactAsync(session.Id, overflow: false, CancellationToken.None);

        // Messages should NOT be deleted
        var messages = await _sessionService.GetMessagesAsync(session.Id, CancellationToken.None);
        // 20 original + 1 summary = 21
        messages.Count.ShouldBe(21);

        // Compacted tool parts should have CompactedTime set
        var compactedToolParts = messages
            .Take(15) // First 15 were compacted (20 - 5 kept)
            .SelectMany(m => m.Parts)
            .OfType<ToolPart>()
            .Where(t => t.State is CompletedToolCallState)
            .ToList();

        compactedToolParts.ShouldNotBeEmpty();
        foreach (var tp in compactedToolParts)
        {
            var state = (CompletedToolCallState)tp.State;
            state.CompactedTime.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task Compact_RedactsToolOutputsInTranscript()
    {
        // Create session with sensitive tool output
        var session = await _sessionService.CreateSessionAsync("/workspace", "Test", CancellationToken.None);

        for (var i = 0; i < 20; i++)
        {
            var isUser = i % 2 == 0;
            NuCodeMessage msg = isUser
                ? new UserMessage(MessageId.New(), session.Id, DateTimeOffset.UtcNow, "build")
                : new AssistantMessage(
                    MessageId.New(), session.Id, DateTimeOffset.UtcNow,
                    ParentId: MessageId.New(), Agent: "build",
                    ProviderId: "test", ModelId: "test-model");
            await _sessionService.UpsertMessageAsync(msg, CancellationToken.None);

            if (!isUser)
            {
                var toolPart = new ToolPart(
                    PartId.New(), session.Id, msg.Id, $"call-{i}", "secret_tool",
                    new CompletedToolCallState(
                        System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty,
                        "SECRET_API_KEY=abc123", "Secret tool", System.Collections.Immutable.ImmutableDictionary<string, object?>.Empty,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
                await _sessionService.UpsertPartAsync(toolPart, CancellationToken.None);
            }
            else
            {
                var textPart = new TextPart(PartId.New(), session.Id, msg.Id, $"Message {i}");
                await _sessionService.UpsertPartAsync(textPart, CancellationToken.None);
            }
        }

        // Use a chat client that captures the transcript sent to it
        var capturingClient = new CapturingChatClient();
        var service = CreateService(
            new CompactionConfig { Auto = true, Prune = true, RecentMessagesToKeep = 5 },
            capturingClient);

        await service.CompactAsync(session.Id, overflow: false, CancellationToken.None);

        // The transcript sent to the LLM should NOT contain the secret
        capturingClient.LastTranscript.ShouldNotBeNull();
        capturingClient.LastTranscript.ShouldNotContain("SECRET_API_KEY");
        capturingClient.LastTranscript.ShouldContain("[tool result omitted]");
    }

    // ── Test doubles ──

    private sealed class SummarizingChatClient : IChatClient
    {
        private readonly string _summary;

        public SummarizingChatClient(string summary) => _summary = summary;

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _summary)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class CancellingCompactionPlugin : INuCodePlugin
    {
        public string Name => "test-cancel-compaction";

        public NuCodeHookCollection Initialize(IServiceProvider services)
        {
            var hooks = new NuCodeHookCollection();
            hooks.On(BuiltInHooks.BeforeCompaction, (input, output) =>
            {
                output.Cancel = true;
                return Task.CompletedTask;
            });
            return hooks;
        }
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("LLM failure");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public string? LastTranscript { get; private set; }

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // Capture the user message (the transcript)
            var userMsg = messages.LastOrDefault(m => m.Role == ChatRole.User);
            LastTranscript = userMsg?.Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
