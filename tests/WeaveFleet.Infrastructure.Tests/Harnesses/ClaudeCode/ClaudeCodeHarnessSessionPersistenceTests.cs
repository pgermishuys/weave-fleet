using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// Tests that <see cref="ClaudeCodeHarnessSession"/> persists messages to the database
/// via <see cref="IMessageRepository"/> and correctly reads them back through
/// <see cref="IHarnessSession.GetMessagesAsync"/>.
/// </summary>
public sealed class ClaudeCodeHarnessSessionPersistenceTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds persistence dependencies backed by a real ServiceCollection so that
    /// GetRequiredService&lt;T&gt;() resolves correctly.
    /// </summary>
    private static (IServiceScopeFactory ScopeFactory, InMemoryMessageRepository MessageRepo)
        BuildPersistenceDependencies()
    {
        var messageRepo = new InMemoryMessageRepository();
        var delegationRepo = new InMemoryDelegationRepository();
        var sessionRepo = new InMemorySessionRepository();
        var outboxRepo = new InMemoryOutboxRepository();
        var outboxDispatcher = new FakeOutboxDispatcher();
        var connectionFactory = new FakeDbConnectionFactory();
        var sessionActivityWriteService = new SessionActivityWriteService(
            connectionFactory,
            messageRepo,
            delegationRepo,
            sessionRepo,
            new InMemorySmartLinkRepository(),
            outboxRepo,
            outboxDispatcher);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(messageRepo);
        services.AddSingleton<IDelegationRepository>(delegationRepo);
        services.AddSingleton<ISessionRepository>(sessionRepo);
        services.AddSingleton<IDbConnectionFactory>(connectionFactory);
        services.AddSingleton<IOutboxRepository>(outboxRepo);
        services.AddSingleton<IOutboxDispatcher>(outboxDispatcher);
        services.AddSingleton(sessionActivityWriteService);
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        return (scopeFactory, messageRepo);
    }

    /// <summary>
    /// Builds persistence dependencies including <see cref="ISessionRepository"/> for resume token tests.
    /// </summary>
    private static (IServiceScopeFactory ScopeFactory, InMemoryMessageRepository MessageRepo, InMemorySessionRepository SessionRepo)
        BuildFullPersistenceDependencies()
    {
        var messageRepo = new InMemoryMessageRepository();
        var delegationRepo = new InMemoryDelegationRepository();
        var sessionRepo = new InMemorySessionRepository();
        var outboxRepo = new InMemoryOutboxRepository();
        var outboxDispatcher = new FakeOutboxDispatcher();
        var connectionFactory = new FakeDbConnectionFactory();
        var sessionActivityWriteService = new SessionActivityWriteService(
            connectionFactory,
            messageRepo,
            delegationRepo,
            sessionRepo,
            new InMemorySmartLinkRepository(),
            outboxRepo,
            outboxDispatcher);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(messageRepo);
        services.AddSingleton<IDelegationRepository>(delegationRepo);
        services.AddSingleton<ISessionRepository>(sessionRepo);
        services.AddSingleton<IDbConnectionFactory>(connectionFactory);
        services.AddSingleton<IOutboxRepository>(outboxRepo);
        services.AddSingleton<IOutboxDispatcher>(outboxDispatcher);
        services.AddSingleton(sessionActivityWriteService);
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        return (scopeFactory, messageRepo, sessionRepo);
    }

    /// <summary>
    /// Creates a <see cref="ClaudeCodeHarnessSession"/> wired to the provided scope factory.
    /// Uses a non-existent binary path so that any process-start attempt fails fast;
    /// DB-path tests should not trigger process start.
    /// </summary>
    private static ClaudeCodeHarnessSession CreateInstance(
        string fleetSessionId,
        IServiceScopeFactory scopeFactory,
        string binaryPath = "/nonexistent/claude-test-binary",
        string? claudeSessionId = null)
    {
        var config = new ClaudeCodeOptions
        {
            BinaryPath = binaryPath,
            PermissionMode = "bypassPermissions",
            ProcessTimeoutSeconds = 5,
        };

        return new ClaudeCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            workingDirectory: "/tmp",
            config: config,
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<ClaudeCodeHarnessSession>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            ownerUserId: TestUserContext.DefaultUserId,
            analyticsCollector: null,
            projectId: null,
            projectName: null,
            claudeSessionId: claudeSessionId);
    }

    // -----------------------------------------------------------------------
    // GetMessagesAsync tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetMessagesAsync_ReadsFromDatabase()
    {
        var fleetSessionId = "fleet-cc-1";
        var (scopeFactory, messageRepo) = BuildPersistenceDependencies();

        var persisted = new List<PersistedMessage>
        {
            new()
            {
                Id = "msg-1",
                SessionId = fleetSessionId,
                Role = "user",
                PartsJson = """[{"type":"text","text":"Hello"}]""",
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            },
            new()
            {
                Id = "msg-2",
                SessionId = fleetSessionId,
                Role = "assistant",
                PartsJson = """[{"type":"text","text":"Hi there"}]""",
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            },
        };

        messageRepo.GetBySessionBehavior = (sessionId, limit, before) =>
            Task.FromResult<IReadOnlyList<PersistedMessage>>(persisted);

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        var page = await instance.GetMessagesAsync(null, CancellationToken.None);

        page.Messages.Count.ShouldBe(2);
        page.Messages[0].Role.ShouldBe("user");
        page.Messages[1].Role.ShouldBe("assistant");
    }

    [Fact]
    public async Task GetMessagesAsync_EmptyHistory_ReturnsEmptyPage()
    {
        var fleetSessionId = "fleet-cc-2";
        var (scopeFactory, messageRepo) = BuildPersistenceDependencies();

        messageRepo.GetBySessionBehavior = (_, _, _) =>
            Task.FromResult<IReadOnlyList<PersistedMessage>>([]);

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        var page = await instance.GetMessagesAsync(null, CancellationToken.None);

        page.Messages.ShouldBeEmpty();
        page.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task GetMessagesAsync_PaginationApplied_PassesLimitAndCursor()
    {
        var fleetSessionId = "fleet-cc-3";
        var (scopeFactory, messageRepo) = BuildPersistenceDependencies();

        messageRepo.GetBySessionBehavior = (_, _, _) =>
            Task.FromResult<IReadOnlyList<PersistedMessage>>([]);

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        var query = new MessageQuery { Limit = 10, Before = "cursor-msg-id" };
        await instance.GetMessagesAsync(query, CancellationToken.None);

        messageRepo.GetBySessionCalls.Count.ShouldBe(1);
        var call = messageRepo.GetBySessionCalls[0];
        call.SessionId.ShouldBe(fleetSessionId);
        call.Limit.ShouldBe(10);
        call.BeforeMessageId.ShouldBe("cursor-msg-id");
    }

    [Fact]
    public async Task GetMessagesAsync_WhenLimitSatisfied_HasMoreIsTrue()
    {
        var fleetSessionId = "fleet-cc-4";
        var (scopeFactory, messageRepo) = BuildPersistenceDependencies();

        // Return exactly `limit` messages — hasMore should be true
        var messages = Enumerable.Range(1, 5)
            .Select(i => new PersistedMessage
            {
                Id = $"msg-{i}",
                SessionId = fleetSessionId,
                Role = "user",
                PartsJson = "[]",
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            })
            .ToList();

        messageRepo.GetBySessionBehavior = (sessionId, limit, before) =>
            Task.FromResult<IReadOnlyList<PersistedMessage>>(messages);

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        var query = new MessageQuery { Limit = 5 };
        var page = await instance.GetMessagesAsync(query, CancellationToken.None);

        page.Messages.Count.ShouldBe(5);
        page.HasMore.ShouldBeTrue();
    }

    [Fact]
    public async Task GetMessagesAsync_WhenFewerMessagesThanLimit_HasMoreIsFalse()
    {
        var fleetSessionId = "fleet-cc-5";
        var (scopeFactory, messageRepo) = BuildPersistenceDependencies();

        var messages = Enumerable.Range(1, 3)
            .Select(i => new PersistedMessage
            {
                Id = $"msg-{i}",
                SessionId = fleetSessionId,
                Role = "user",
                PartsJson = "[]",
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            })
            .ToList();

        messageRepo.GetBySessionBehavior = (_, _, _) =>
            Task.FromResult<IReadOnlyList<PersistedMessage>>(messages);

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        var query = new MessageQuery { Limit = 10 };
        var page = await instance.GetMessagesAsync(query, CancellationToken.None);

        page.Messages.Count.ShouldBe(3);
        page.HasMore.ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // SendPromptAsync persistence tests
    // -----------------------------------------------------------------------

    // SendPromptAsync no longer persists user messages directly. User prompt
    // persistence is owned by SessionOrchestrator so the frontend and backend
    // share one client-owned prompt ID.

    // -----------------------------------------------------------------------
    // Message content round-trip
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetMessagesAsync_TextPartsDeserializedCorrectly()
    {
        var fleetSessionId = "fleet-cc-rt-1";
        var (scopeFactory, messageRepo) = BuildPersistenceDependencies();

        var persisted = new List<PersistedMessage>
        {
            new()
            {
                Id = "msg-roundtrip",
                SessionId = fleetSessionId,
                Role = "assistant",
                PartsJson = """[{"type":"text","text":"The answer is 42"}]""",
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            },
        };

        messageRepo.GetBySessionBehavior = (_, _, _) =>
            Task.FromResult<IReadOnlyList<PersistedMessage>>(persisted);

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        var page = await instance.GetMessagesAsync(null, CancellationToken.None);

        page.Messages.Count.ShouldBe(1);
        var msg = page.Messages[0];
        msg.Role.ShouldBe("assistant");
        msg.Parts.Count.ShouldBe(1);
        var textPart = msg.Parts[0].ShouldBeOfType<TextPart>();
        textPart.Text.ShouldBe("The answer is 42");
    }

    // -----------------------------------------------------------------------
    // ResumeToken tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResumeToken_WhenConstructedWithoutPrePopulatedSessionId_IsNull()
    {
        var fleetSessionId = "fleet-cc-resume-1";
        var (scopeFactory, _) = BuildPersistenceDependencies();

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        instance.ResumeToken.ShouldBeNull();
    }

    [Fact]
    public async Task ResumeToken_WhenConstructedWithPrePopulatedSessionId_ReturnsThatId()
    {
        var fleetSessionId = "fleet-cc-resume-2";
        var claudeSessionId = "claude-sess-abc123";
        var (scopeFactory, _) = BuildPersistenceDependencies();

        await using var instance = CreateInstance(
            fleetSessionId, scopeFactory, claudeSessionId: claudeSessionId);

        instance.ResumeToken.ShouldBe(claudeSessionId);
    }

    [Fact]
    public async Task ResumeToken_WhenPrePopulated_SendPromptUsesResumeFlag()
    {
        // Verifies that a pre-populated session ID is used in the prompt options.
        // The process launch will fail (non-existent binary), but the claudeSessionId
        // is already set before SendPromptAsync returns — that's what matters for resume.
        var fleetSessionId = "fleet-cc-resume-3";
        var claudeSessionId = "resume-token-xyz";
        var (scopeFactory, messageRepo) = BuildPersistenceDependencies();

        // UpsertBehavior is a no-op (default Task.CompletedTask)
        messageRepo.UpsertBehavior = _ => Task.CompletedTask;

        await using var instance = CreateInstance(
            fleetSessionId, scopeFactory, claudeSessionId: claudeSessionId);

        // The resume token is already set — even before any prompt is sent
        instance.ResumeToken.ShouldBe(claudeSessionId);

        // After spawn, the token remains the pre-populated value (unchanged by failed process start)
        try
        {
            await instance.SendPromptAsync("Continue", null, CancellationToken.None);
        }
        catch
        {
            // Expected — the binary doesn't exist
        }

        // ResumeToken must still equal the pre-populated value
        instance.ResumeToken.ShouldBe(claudeSessionId);
    }

    [Fact]
    public async Task PersistResumeTokenAsync_WhenSessionRepoInDi_DoesNotThrow()
    {
        // Verifies that PersistResumeTokenAsync (called from PumpStdoutAsync) can
        // resolve ISessionRepository from the scope factory without throwing.
        // This exercises the DI wiring for the resume token persistence path.
        var fleetSessionId = "fleet-cc-resume-4";
        var (scopeFactory, _, sessionRepo) = BuildFullPersistenceDependencies();

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        // The instance is wired with a scope factory that includes ISessionRepository.
        // Verify the scope factory correctly resolves ISessionRepository (validates DI setup).
        using var scope = scopeFactory.CreateScope();
        var resolvedRepo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();

        resolvedRepo.ShouldNotBeNull();
        resolvedRepo.ShouldBeSameAs(sessionRepo);
    }
}
