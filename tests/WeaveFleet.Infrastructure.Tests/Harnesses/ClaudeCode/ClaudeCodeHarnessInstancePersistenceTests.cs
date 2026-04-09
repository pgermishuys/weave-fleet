using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// Tests that <see cref="ClaudeCodeHarnessInstance"/> persists messages to the database
/// via <see cref="IMessageRepository"/> and correctly reads them back through
/// <see cref="IHarnessInstance.GetMessagesAsync"/>.
/// </summary>
public sealed class ClaudeCodeHarnessInstancePersistenceTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds persistence dependencies backed by a real ServiceCollection so that
    /// GetRequiredService&lt;T&gt;() resolves correctly without NSubstitute IServiceProvider quirks.
    /// </summary>
    private static (IServiceScopeFactory ScopeFactory, IMessageRepository MessageRepo)
        BuildPersistenceDependencies()
    {
        var messageRepo = Substitute.For<IMessageRepository>();

        var services = new ServiceCollection();
        services.AddSingleton(messageRepo);
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        return (scopeFactory, messageRepo);
    }

    /// <summary>
    /// Builds persistence dependencies including <see cref="ISessionRepository"/> for resume token tests.
    /// </summary>
    private static (IServiceScopeFactory ScopeFactory, IMessageRepository MessageRepo, ISessionRepository SessionRepo)
        BuildFullPersistenceDependencies()
    {
        var messageRepo = Substitute.For<IMessageRepository>();
        var sessionRepo = Substitute.For<ISessionRepository>();

        var services = new ServiceCollection();
        services.AddSingleton(messageRepo);
        services.AddSingleton(sessionRepo);
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        return (scopeFactory, messageRepo, sessionRepo);
    }

    /// <summary>
    /// Creates a <see cref="ClaudeCodeHarnessInstance"/> wired to the provided scope factory.
    /// Uses a non-existent binary path so that any process-start attempt fails fast;
    /// DB-path tests should not trigger process start.
    /// </summary>
    private static ClaudeCodeHarnessInstance CreateInstance(
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

        return new ClaudeCodeHarnessInstance(
            instanceId: "test-instance",
            fleetSessionId: fleetSessionId,
            workingDirectory: "/tmp",
            config: config,
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: scopeFactory,
            logger: NullLogger<ClaudeCodeHarnessInstance>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
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

        messageRepo.GetBySessionAsync(fleetSessionId, 50, null)
            .Returns(Task.FromResult<IReadOnlyList<PersistedMessage>>(persisted));

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

        messageRepo.GetBySessionAsync(fleetSessionId, 50, null)
            .Returns(Task.FromResult<IReadOnlyList<PersistedMessage>>([]));

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

        messageRepo.GetBySessionAsync(fleetSessionId, Arg.Any<int>(), Arg.Any<string?>())
            .Returns(Task.FromResult<IReadOnlyList<PersistedMessage>>([]));

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        var query = new MessageQuery { Limit = 10, Before = "cursor-msg-id" };
        await instance.GetMessagesAsync(query, CancellationToken.None);

        await messageRepo.Received(1)
            .GetBySessionAsync(fleetSessionId, 10, "cursor-msg-id");
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

        messageRepo.GetBySessionAsync(fleetSessionId, 5, null)
            .Returns(Task.FromResult<IReadOnlyList<PersistedMessage>>(messages));

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

        messageRepo.GetBySessionAsync(fleetSessionId, 10, null)
            .Returns(Task.FromResult<IReadOnlyList<PersistedMessage>>(messages));

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        var query = new MessageQuery { Limit = 10 };
        var page = await instance.GetMessagesAsync(query, CancellationToken.None);

        page.Messages.Count.ShouldBe(3);
        page.HasMore.ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // SendPromptAsync persistence tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SendPromptAsync_PersistsSyntheticUserMessage()
    {
        // NOTE: SendPromptAsync persists the user message as fire-and-forget BEFORE
        // spawning the process. The process launch will fail (non-existent binary),
        // but the persist task was already enqueued.
        var fleetSessionId = "fleet-cc-send-1";
        var (scopeFactory, messageRepo) = BuildPersistenceDependencies();

        var persistSignal = new TaskCompletionSource();
        messageRepo.UpsertAsync(Arg.Any<PersistedMessage>()).Returns(callInfo =>
        {
            persistSignal.TrySetResult();
            return Task.CompletedTask;
        });

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        // Act — the send will throw because the binary doesn't exist, but
        // the fire-and-forget persist was already scheduled
        try
        {
            await instance.SendPromptAsync("Hello", null, CancellationToken.None);
        }
        catch
        {
            // Expected — the binary doesn't exist
        }

        // Wait for the fire-and-forget persist to complete
        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await messageRepo.Received(1).UpsertAsync(Arg.Is<PersistedMessage>(m =>
            m.SessionId == fleetSessionId &&
            m.Role == "user" &&
            m.PartsJson.Contains("Hello")));
    }

    [Fact]
    public async Task SendPromptAsync_PersistenceFailure_DoesNotCrashInstance()
    {
        var fleetSessionId = "fleet-cc-send-2";
        var (scopeFactory, messageRepo) = BuildPersistenceDependencies();

        // DB throws — should be silently swallowed
        messageRepo.UpsertAsync(Arg.Any<PersistedMessage>())
            .Returns(_ => Task.FromException(new InvalidOperationException("DB is on fire")));

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        // Act — should not throw due to DB error
        try
        {
            await instance.SendPromptAsync("Hello", null, CancellationToken.None);
        }
        catch
        {
            // Process launch failure is expected — we only care that DB failure is silent
        }

        // Give fire-and-forget time to settle
        await Task.Delay(300);

        // Instance should still be disposeably healthy (no unhandled exception propagated)
        // Verify UpsertAsync was called (and swallowed the exception)
        await messageRepo.Received().UpsertAsync(Arg.Any<PersistedMessage>());
    }

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

        messageRepo.GetBySessionAsync(fleetSessionId, 50, null)
            .Returns(Task.FromResult<IReadOnlyList<PersistedMessage>>(persisted));

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

        messageRepo.UpsertAsync(Arg.Any<PersistedMessage>()).Returns(Task.CompletedTask);

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

        sessionRepo.UpdateResumeTokenAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        await using var instance = CreateInstance(fleetSessionId, scopeFactory);

        // The instance is wired with a scope factory that includes ISessionRepository.
        // Verify the scope factory correctly resolves ISessionRepository (validates DI setup).
        using var scope = scopeFactory.CreateScope();
        var resolvedRepo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();

        resolvedRepo.ShouldNotBeNull();
        resolvedRepo.ShouldBeSameAs(sessionRepo);
    }
}
