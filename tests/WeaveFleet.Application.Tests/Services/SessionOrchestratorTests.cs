using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class SessionOrchestratorTests
{
    private readonly IHarnessRegistry _harnessRegistry = Substitute.For<IHarnessRegistry>();
    private readonly IHarness _harness = Substitute.For<IHarness>();
    private readonly IHarnessInstance _harnessInstance = Substitute.For<IHarnessInstance>();
    private readonly ISessionRepository _sessionRepo = Substitute.For<ISessionRepository>();
    private readonly ISessionCallbackRepository _callbackRepo = Substitute.For<ISessionCallbackRepository>();
    private readonly IProjectRepository _projectRepo = Substitute.For<IProjectRepository>();
    private readonly IWorkspaceRepository _workspaceRepo = Substitute.For<IWorkspaceRepository>();
    private readonly IInstanceRepository _instanceRepo = Substitute.For<IInstanceRepository>();
    private readonly IEventBroadcaster _eventBroadcaster = Substitute.For<IEventBroadcaster>();
    private readonly IAnalyticsCollector _analyticsCollector = Substitute.For<IAnalyticsCollector>();
    private readonly IMessageRepository _messageRepo = Substitute.For<IMessageRepository>();
    private readonly InstanceTracker _tracker = new();
    private readonly SessionOrchestrator _sut;

    public SessionOrchestratorTests()
    {
        var workspaceService = new WorkspaceService(
            _workspaceRepo,
            NullLogger<WorkspaceService>.Instance);

        var instanceService = new InstanceService(_instanceRepo, _sessionRepo);

        _sut = new SessionOrchestrator(
            workspaceService,
            instanceService,
            _harnessRegistry,
            _tracker,
            _sessionRepo,
            _callbackRepo,
            _projectRepo,
            _eventBroadcaster,
            _analyticsCollector,
            _messageRepo,
            new FleetOptions(),
            NullLogger<SessionOrchestrator>.Instance);

        // Default harness instance id
        _harnessInstance.InstanceId.Returns("inst-1");
        _harnessInstance.HarnessType.Returns("opencode");
        _harnessInstance.Status.Returns(HarnessInstanceStatus.Running);
    }

    // ── CreateSessionAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSessionAsync_WhenHarnessNotFound_ReturnsFailure()
    {
        _harnessRegistry.GetByType("opencode").Returns((IHarness?)null);

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = "/tmp/project"
        });

        Assert.True(result.IsFailure);
        Assert.Contains("Harness", result.Error.Description);
    }

    [Fact]
    public async Task CreateSessionAsync_HappyPath_InsertsSessionAndReturnsResult()
    {
        // Arrange
        _harnessRegistry.GetByType("opencode").Returns(_harness);
        _harness.SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>())
            .Returns(_harnessInstance);
        _projectRepo.ListAsync().Returns(new List<Project>
        {
            new() { Id = "scratch-1", Name = "Scratch", Type = "scratch", Position = 0,
                    CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" }
        });
        _instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        _sessionRepo.InsertAsync(Arg.Any<Session>()).Returns(Task.CompletedTask);

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = "/tmp/project",
            Title = "My Session"
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("My Session", result.Value.Session.Title);
        Assert.Equal("inst-1", result.Value.InstanceId);
        await _sessionRepo.Received(1).InsertAsync(Arg.Is<Session>(s =>
            s.Title == "My Session" && s.ProjectId == "scratch-1"));
    }

    [Fact]
    public async Task CreateSessionAsync_WhenSpawnThrows_ReturnsUnexpectedError()
    {
        _harnessRegistry.GetByType("opencode").Returns(_harness);
        _harness.SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("process failed"));
        _projectRepo.ListAsync().Returns(new List<Project>());

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = "/tmp/project"
        });

        Assert.True(result.IsFailure);
        Assert.Equal(FleetError.Unexpected.Code, result.Error.Code);
    }

    // ── PromptSessionAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task PromptSessionAsync_WhenSessionNotFound_ReturnsFailure()
    {
        _sessionRepo.GetByIdAsync("missing").Returns((Session?)null);

        var result = await _sut.PromptSessionAsync("missing", "hello");

        Assert.True(result.IsFailure);
        Assert.Contains("Session", result.Error.Description);
    }

    [Fact]
    public async Task PromptSessionAsync_WhenInstanceNotTracked_ReturnsFailure()
    {
        _sessionRepo.GetByIdAsync("s1").Returns(new Session
        {
            Id = "s1", InstanceId = "inst-99", Title = "T", Status = "active",
            Directory = "/tmp", CreatedAt = "2026-01-01"
        });

        var result = await _sut.PromptSessionAsync("s1", "hello");

        Assert.True(result.IsFailure);
        Assert.Contains("Instance", result.Error.Description);
    }

    [Fact]
    public async Task PromptSessionAsync_HappyPath_SendsPrompt()
    {
        _sessionRepo.GetByIdAsync("s1").Returns(new Session
        {
            Id = "s1", InstanceId = "inst-1", Title = "T", Status = "active",
            Directory = "/tmp", CreatedAt = "2026-01-01"
        });
        _tracker.Register("inst-1", _harnessInstance);

        var result = await _sut.PromptSessionAsync("s1", "hello");

        Assert.True(result.IsSuccess);
        await _harnessInstance.Received(1).SendPromptAsync("hello", null, Arg.Any<CancellationToken>());
    }

    // ── GetSessionMessagesAsync ────────────────────────────────────────────────

    private static Session MakeSession(string id, string instanceId) => new()
    {
        Id = id, InstanceId = instanceId, Title = "T",
        Status = "active", Directory = "/tmp", CreatedAt = "2026-01-01"
    };

    private static List<Domain.Entities.PersistedMessage> MakePersistedMessages(string sessionId, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Domain.Entities.PersistedMessage
            {
                Id = $"msg-{i}",
                SessionId = sessionId,
                Role = "assistant",
                PartsJson = """[{"type":"text","text":"hello"}]""",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(i).ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            })
            .ToList();

    [Fact]
    public async Task GetSessionMessages_LiveInstance_ProxiesToInstance()
    {
        var session = MakeSession("s1", "inst-1");
        _sessionRepo.GetByIdAsync("s1").Returns(session);
        _tracker.Register("inst-1", _harnessInstance);
        _harnessInstance.GetMessagesAsync(Arg.Any<MessageQuery?>(), Arg.Any<CancellationToken>())
            .Returns(new MessagePage([
                new HarnessMessage { Id = "m1", Role = "user", Parts = [], Timestamp = DateTimeOffset.UtcNow }
            ], false));

        var result = await _sut.GetSessionMessagesAsync("s1");

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Messages);
        await _messageRepo.DidNotReceive().GetBySessionAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task GetSessionMessages_NoInstance_FallsBackToDb()
    {
        var session = MakeSession("s1", "inst-missing");
        _sessionRepo.GetByIdAsync("s1").Returns(session);
        // No instance registered — tracker returns null
        var persisted = MakePersistedMessages("s1", 3);
        _messageRepo.GetBySessionAsync("s1", Arg.Any<int>(), null).Returns(persisted);

        var result = await _sut.GetSessionMessagesAsync("s1");

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.Messages.Count);
    }

    [Fact]
    public async Task GetSessionMessages_NoInstance_NoDbMessages_ReturnsEmptyPage()
    {
        var session = MakeSession("s1", "inst-missing");
        _sessionRepo.GetByIdAsync("s1").Returns(session);
        _messageRepo.GetBySessionAsync("s1", Arg.Any<int>(), null)
            .Returns(new List<Domain.Entities.PersistedMessage>());

        var result = await _sut.GetSessionMessagesAsync("s1");

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Messages);
        Assert.False(result.Value.HasMore);
    }

    [Fact]
    public async Task GetSessionMessages_LiveInstanceThrows_FallsBackToDb()
    {
        var session = MakeSession("s1", "inst-1");
        _sessionRepo.GetByIdAsync("s1").Returns(session);
        _tracker.Register("inst-1", _harnessInstance);
        _harnessInstance.GetMessagesAsync(Arg.Any<MessageQuery?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var persisted = MakePersistedMessages("s1", 2);
        _messageRepo.GetBySessionAsync("s1", Arg.Any<int>(), null).Returns(persisted);

        var result = await _sut.GetSessionMessagesAsync("s1");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Messages.Count);
    }

    [Fact]
    public async Task GetSessionMessages_SessionNotFound_ReturnsNotFound()
    {
        _sessionRepo.GetByIdAsync("ghost").Returns((Session?)null);

        var result = await _sut.GetSessionMessagesAsync("ghost");

        Assert.True(result.IsFailure);
        Assert.Contains("Session", result.Error.Description);
    }

    [Fact]
    public async Task GetSessionMessages_UsesHistoryPageSizeForDbFallback()
    {
        var session = MakeSession("s2", "inst-missing");
        _sessionRepo.GetByIdAsync("s2").Returns(session);

        // Return limit+1 rows to trigger hasMore
        _messageRepo.GetBySessionAsync("s2", Arg.Any<int>(), null)
            .Returns(callInfo =>
            {
                var limit = callInfo.ArgAt<int>(1);
                return MakePersistedMessages("s2", limit + 1);
            });

        var result = await _sut.GetSessionMessagesAsync("s2");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.HasMore);
        // The DB was called with HistoryMessagePageSize+1 (default 10 → fetches 11)
        await _messageRepo.Received(1).GetBySessionAsync("s2", 11, null);
    }

    [Fact]
    public async Task GetSessionMessages_UsesLivePageSizeForLiveInstance()
    {
        var session = MakeSession("s3", "inst-1");
        _sessionRepo.GetByIdAsync("s3").Returns(session);
        _tracker.Register("inst-1", _harnessInstance);

        MessageQuery? capturedQuery = null;
        _harnessInstance
            .GetMessagesAsync(Arg.Do<MessageQuery?>(q => capturedQuery = q), Arg.Any<CancellationToken>())
            .Returns(new MessagePage([], false));

        await _sut.GetSessionMessagesAsync("s3");

        // Default LiveMessagePageSize is 10
        Assert.NotNull(capturedQuery);
        Assert.Equal(10, capturedQuery.Limit);
    }

    // ── Harness type tracking ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateSessionAsync_StoresHarnessType()
    {
        // Arrange — request specifies "claude-code"
        _harnessRegistry.GetByType("claude-code").Returns(_harness);
        _harness.SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>())
            .Returns(_harnessInstance);
        _projectRepo.ListAsync().Returns(new List<Project>());
        _instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        _sessionRepo.InsertAsync(Arg.Any<Session>()).Returns(Task.CompletedTask);

        // Act
        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = "/tmp/project",
            HarnessType = "claude-code"
        });

        // Assert — session stored with correct harness type
        Assert.True(result.IsSuccess);
        await _sessionRepo.Received(1).InsertAsync(Arg.Is<Session>(s =>
            s.HarnessType == "claude-code"));
    }

    [Fact]
    public async Task CreateSessionAsync_DefaultsToOpenCode()
    {
        // Arrange — no HarnessType in request
        _harnessRegistry.GetByType("opencode").Returns(_harness);
        _harness.SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>())
            .Returns(_harnessInstance);
        _projectRepo.ListAsync().Returns(new List<Project>());
        _instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        _sessionRepo.InsertAsync(Arg.Any<Session>()).Returns(Task.CompletedTask);

        // Act
        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = "/tmp/project"
            // HarnessType omitted → should default to "opencode"
        });

        // Assert — default harness type is opencode
        Assert.True(result.IsSuccess);
        await _sessionRepo.Received(1).InsertAsync(Arg.Is<Session>(s =>
            s.HarnessType == "opencode"));
    }

    [Fact]
    public async Task ResumeSessionAsync_UsesStoredHarnessType()
    {
        // Arrange — session has harness type "claude-code" in DB
        var session = new Session
        {
            Id = "s-resume",
            InstanceId = "inst-old",
            WorkspaceId = "ws-1",
            HarnessType = "claude-code",
            Title = "T",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        };
        _sessionRepo.GetByIdAsync("s-resume").Returns(session);
        _workspaceRepo.GetByIdAsync("ws-1").Returns(new Domain.Entities.Workspace
        {
            Id = "ws-1",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });
        _harnessRegistry.GetByType("claude-code").Returns(_harness);
        _harness.Capabilities.Returns(new HarnessCapabilities { SupportsResume = false });
        _harness.SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>())
            .Returns(_harnessInstance);
        _instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        _sessionRepo.UpdateForResumeAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.ResumeSessionAsync("s-resume");

        // Assert — harness resolved using stored type, not default "opencode"
        Assert.True(result.IsSuccess);
        _harnessRegistry.Received(1).GetByType("claude-code");
        _harnessRegistry.DidNotReceive().GetByType("opencode");
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenTokenPresent_AndSupportsResume_CallsResumeAsync()
    {
        // Arrange — session has a resume token and harness supports resume
        var session = new Session
        {
            Id = "s-resume-token",
            InstanceId = "inst-old",
            WorkspaceId = "ws-2",
            HarnessType = "opencode",
            HarnessResumeToken = "existing-session-token",
            Title = "T",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        };
        _sessionRepo.GetByIdAsync("s-resume-token").Returns(session);
        _workspaceRepo.GetByIdAsync("ws-2").Returns(new Domain.Entities.Workspace
        {
            Id = "ws-2",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });
        _harnessRegistry.GetByType("opencode").Returns(_harness);
        _harness.Capabilities.Returns(new HarnessCapabilities { SupportsResume = true });
        _harness.ResumeAsync(Arg.Any<HarnessResumeOptions>(), Arg.Any<CancellationToken>())
            .Returns(_harnessInstance);
        _instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        _sessionRepo.UpdateForResumeAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.ResumeSessionAsync("s-resume-token");

        // Assert — ResumeAsync called with correct token; SpawnAsync NOT called
        Assert.True(result.IsSuccess);
        await _harness.Received(1).ResumeAsync(
            Arg.Is<HarnessResumeOptions>(o => o.ResumeToken == "existing-session-token"),
            Arg.Any<CancellationToken>());
        await _harness.DidNotReceive().SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenTokenNull_FallsBackToSpawnAsync()
    {
        // Arrange — session has no resume token → SpawnAsync is called
        var session = new Session
        {
            Id = "s-resume-notok",
            InstanceId = "inst-old",
            WorkspaceId = "ws-3",
            HarnessType = "opencode",
            HarnessResumeToken = null,
            Title = "T",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        };
        _sessionRepo.GetByIdAsync("s-resume-notok").Returns(session);
        _workspaceRepo.GetByIdAsync("ws-3").Returns(new Domain.Entities.Workspace
        {
            Id = "ws-3",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });
        _harnessRegistry.GetByType("opencode").Returns(_harness);
        _harness.Capabilities.Returns(new HarnessCapabilities { SupportsResume = true });
        _harness.SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>())
            .Returns(_harnessInstance);
        _instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        _sessionRepo.UpdateForResumeAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.ResumeSessionAsync("s-resume-notok");

        // Assert — SpawnAsync used as fallback; ResumeAsync NOT called
        Assert.True(result.IsSuccess);
        await _harness.Received(1).SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>());
        await _harness.DidNotReceive().ResumeAsync(Arg.Any<HarnessResumeOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenTokenPresent_ButSupportsResumeFalse_FallsBackToSpawnAsync()
    {
        // Arrange — token exists but harness doesn't support resume → SpawnAsync fallback
        var session = new Session
        {
            Id = "s-resume-nosupp",
            InstanceId = "inst-old",
            WorkspaceId = "ws-4",
            HarnessType = "opencode",
            HarnessResumeToken = "some-token",
            Title = "T",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        };
        _sessionRepo.GetByIdAsync("s-resume-nosupp").Returns(session);
        _workspaceRepo.GetByIdAsync("ws-4").Returns(new Domain.Entities.Workspace
        {
            Id = "ws-4",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });
        _harnessRegistry.GetByType("opencode").Returns(_harness);
        _harness.Capabilities.Returns(new HarnessCapabilities { SupportsResume = false });
        _harness.SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>())
            .Returns(_harnessInstance);
        _instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        _sessionRepo.UpdateForResumeAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.ResumeSessionAsync("s-resume-nosupp");

        // Assert — SpawnAsync used (SupportsResume = false overrides token)
        Assert.True(result.IsSuccess);
        await _harness.Received(1).SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>());
        await _harness.DidNotReceive().ResumeAsync(Arg.Any<HarnessResumeOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForkSessionAsync_InheritsParentHarnessType()
    {
        // Arrange — parent has "claude-code"
        var parent = new Session
        {
            Id = "s-parent",
            InstanceId = "inst-parent",
            HarnessType = "claude-code",
            Title = "Parent",
            Status = "active",
            Directory = "/tmp/parent",
            ProjectId = null,
            CreatedAt = "2026-01-01"
        };
        _sessionRepo.GetByIdAsync("s-parent").Returns(parent);
        _harnessRegistry.GetByType("claude-code").Returns(_harness);
        _harness.SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>())
            .Returns(_harnessInstance);
        _projectRepo.ListAsync().Returns(new List<Project>());
        _instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        _sessionRepo.InsertAsync(Arg.Any<Session>()).Returns(Task.CompletedTask);

        // Act
        var result = await _sut.ForkSessionAsync("s-parent", "Forked Session");

        // Assert — forked session uses same harness type as parent
        Assert.True(result.IsSuccess);
        await _sessionRepo.Received(1).InsertAsync(Arg.Is<Session>(s =>
            s.HarnessType == "claude-code"));
    }
}
