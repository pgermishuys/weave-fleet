using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
    /// Creates a <see cref="ClaudeCodeHarnessInstance"/> wired to the provided scope factory.
    /// Uses a non-existent binary path so that any process-start attempt fails fast;
    /// DB-path tests should not trigger process start.
    /// </summary>
    private static ClaudeCodeHarnessInstance CreateInstance(
        string fleetSessionId,
        IServiceScopeFactory scopeFactory,
        string binaryPath = "/nonexistent/claude-test-binary")
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
            projectName: null);
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

        Assert.Equal(2, page.Messages.Count);
        Assert.Equal("user", page.Messages[0].Role);
        Assert.Equal("assistant", page.Messages[1].Role);
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

        Assert.Empty(page.Messages);
        Assert.False(page.HasMore);
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

        Assert.Equal(5, page.Messages.Count);
        Assert.True(page.HasMore);
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

        Assert.Equal(3, page.Messages.Count);
        Assert.False(page.HasMore);
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

        Assert.Single(page.Messages);
        var msg = page.Messages[0];
        Assert.Equal("assistant", msg.Role);
        Assert.Single(msg.Parts);
        var textPart = Assert.IsType<TextPart>(msg.Parts[0]);
        Assert.Equal("The answer is 42", textPart.Text);
    }
}
