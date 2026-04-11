using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.SessionSources;
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
    private readonly ISessionSourceUsageRepository _sessionSourceUsageRepo = Substitute.For<ISessionSourceUsageRepository>();
    private readonly ISessionCallbackRepository _callbackRepo = Substitute.For<ISessionCallbackRepository>();
    private readonly IDelegationRepository _delegationRepo = Substitute.For<IDelegationRepository>();
    private readonly IProjectRepository _projectRepo = Substitute.For<IProjectRepository>();
    private readonly IWorkspaceRepository _workspaceRepo = Substitute.For<IWorkspaceRepository>();
    private readonly IWorkspaceRootRepository _workspaceRootRepo = Substitute.For<IWorkspaceRootRepository>();
    private readonly IInstanceRepository _instanceRepo = Substitute.For<IInstanceRepository>();
    private readonly IEventBroadcaster _eventBroadcaster = Substitute.For<IEventBroadcaster>();
    private readonly IAnalyticsCollector _analyticsCollector = Substitute.For<IAnalyticsCollector>();
    private readonly IMessageRepository _messageRepo = Substitute.For<IMessageRepository>();
    private readonly ICredentialStore _credentialStore = Substitute.For<ICredentialStore>();
    private readonly InstanceTracker _tracker = new();
    private readonly IUserContext _userContext = new TestUserContext("user-1");
    private readonly FleetOptions _options = new();
    private readonly DelegationService _delegationService;
    private readonly SessionOrchestrator _sut;

    public SessionOrchestratorTests()
    {
        _workspaceRootRepo.ListAsync().Returns([
            new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") }
        ]);
        var workspaceRootService = new WorkspaceRootService(_workspaceRootRepo, _userContext);
        var workspaceService = new WorkspaceService(
            _workspaceRepo,
            _userContext,
            _options,
            NullLogger<WorkspaceService>.Instance);

        var instanceService = new InstanceService(_instanceRepo, _sessionRepo, _userContext);
        var sessionSourceResolutionService = new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService),
            new ManagedWorkspaceSessionSourceProvider(_options)
        ], _options);
        _delegationService = new DelegationService(_delegationRepo, _eventBroadcaster, _userContext);

        // Default: credential store returns empty bag; harness always reports Ready.
        _credentialStore.GetDecryptedCredentialsAsync(Arg.Any<string>()).Returns([]);
        _harness.PrepareRuntimeAsync(Arg.Any<RuntimePreparationContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new StubLaunchArtifacts())));

        _sut = new SessionOrchestrator(
            workspaceService,
            instanceService,
            sessionSourceResolutionService,
            _harnessRegistry,
            _tracker,
            _sessionRepo,
            _sessionSourceUsageRepo,
            _callbackRepo,
            _delegationRepo,
            _projectRepo,
            _eventBroadcaster,
            _analyticsCollector,
            _messageRepo,
            _delegationService,
            _credentialStore,
            _userContext,
            _options,
            NullLogger<SessionOrchestrator>.Instance);

        // Default harness instance id
        _harnessInstance.InstanceId.Returns("inst-1");
        _harnessInstance.HarnessType.Returns("opencode");
        _harnessInstance.Status.Returns(HarnessInstanceStatus.Running);
        _instanceRepo.GetByIdAsync(Arg.Any<string>()).Returns(callInfo => new Instance
        {
            Id = callInfo.Arg<string>(),
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });
        _instanceRepo.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(Task.CompletedTask);
        _sessionRepo.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(Task.CompletedTask);
        _sessionRepo.ArchiveAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        _sessionRepo.UnarchiveAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
    }

    private void ConfigureHarnessAndScratchProject()
    {
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
    }

    // ── CreateSessionAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSessionAsync_WhenHarnessNotFound_ReturnsFailure()
    {
        _harnessRegistry.GetByType("opencode").Returns((IHarness?)null);
        using var tempDirectory = new TempDirectory();

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path
        });

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("Harness");
    }

    [Fact]
    public async Task CreateSessionAsync_HappyPath_InsertsSessionAndReturnsResult()
    {
        ConfigureHarnessAndScratchProject();
        using var tempDirectory = new TempDirectory();

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path,
            Title = "My Session"
        });

        result.IsSuccess.ShouldBeTrue();
        result.Value.Session.Title.ShouldBe("My Session");
        result.Value.InstanceId.ShouldBe("inst-1");
        result.Value.Session.UserId.ShouldBe("user-1");
        await _sessionRepo.Received(1).InsertAsync(Arg.Is<Session>(s =>
            s.Title == "My Session" && s.ProjectId == "scratch-1" && s.UserId == "user-1"));
    }

    [Fact]
    public async Task CreateSessionAsync_WhenSpawnThrows_ReturnsUnexpectedError()
    {
        _harnessRegistry.GetByType("opencode").Returns(_harness);
        _harness.SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("process failed"));
        _projectRepo.ListAsync().Returns(new List<Project>());
        using var tempDirectory = new TempDirectory();

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path
        });

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(FleetError.Unexpected.Code);
    }

    [Fact]
    public async Task CreateSessionAsync_InCloudModeWithoutDirectory_CreatesManagedWorkspaceSession()
    {
        ConfigureHarnessAndScratchProject();
        using var workspaceRoot = new TempDirectory();
        _options.Cloud = new CloudOptions
        {
            Enabled = true,
            WorkspaceRoot = workspaceRoot.Path
        };

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest());

        result.IsSuccess.ShouldBeTrue();
        await _sessionRepo.Received(1).InsertAsync(Arg.Is<Session>(session =>
            session.Directory.StartsWith(workspaceRoot.Path, StringComparison.OrdinalIgnoreCase)));
        await _sessionSourceUsageRepo.Received(1).InsertAsync(Arg.Is<SessionSourceUsage>(usage =>
            usage.ProviderId == SessionSourceProviderIds.Managed &&
            usage.SourceType == SessionSourceTypeNames.ManagedWorkspace));
    }

    [Fact]
    public async Task PreviewAddSourceToSessionAsync_WhenSourceResolves_ReturnsEnvelope()
    {
        var sessionId = "session-1";
        _sessionRepo.GetByIdAsync(sessionId).Returns(new Session
        {
            Id = sessionId,
            InstanceId = "inst-1",
            WorkspaceId = "ws-1",
            Title = "T",
            Status = "active",
            RetentionStatus = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });

        var result = await _sut.PreviewAddSourceToSessionAsync(sessionId, new SessionSourceSelection
        {
            Key = new SessionSourceKey
            {
                ProviderId = SessionSourceProviderIds.Local,
                SourceType = SessionSourceTypeNames.ExternalDocument,
                ActionId = SessionSourceActions.AddToSession,
                ContractVersion = 1
            },
            Input = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                resourceId = "doc-1",
                selection = "summary"
            })
        });

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task AddSourceToSessionAsync_WhenConfirmFalse_ReturnsValidationFailure()
    {
        var result = await _sut.AddSourceToSessionAsync("session-1", new SessionSourceSelection
        {
            Key = SessionSourceCatalog.ExternalDocumentAddToSession.Key,
            Input = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                resourceId = "doc-1"
            })
        }, confirm: false);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.SessionSource.Confirm");
    }

    [Fact]
    public async Task AddSourceToSessionAsync_WhenSessionArchived_ReturnsValidationFailure()
    {
        _sessionRepo.GetByIdAsync("session-archived").Returns(new Session
        {
            Id = "session-archived",
            InstanceId = "inst-1",
            WorkspaceId = "ws-1",
            Title = "Archived",
            Status = "stopped",
            RetentionStatus = "archived",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });

        var result = await _sut.AddSourceToSessionAsync("session-archived", new SessionSourceSelection
        {
            Key = SessionSourceCatalog.ExternalDocumentAddToSession.Key,
            Input = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                resourceId = "doc-1"
            })
        }, confirm: true);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.RetentionStatus");
        await _sessionSourceUsageRepo.DidNotReceive().InsertAsync(Arg.Any<SessionSourceUsage>());
    }

    // ── PromptSessionAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task PromptSessionAsync_WhenSessionNotFound_ReturnsFailure()
    {
        _sessionRepo.GetByIdAsync("missing").Returns((Session?)null);

        var result = await _sut.PromptSessionAsync("missing", "hello");

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("Session");
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

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("Instance");
    }

    [Fact]
    public async Task PromptSessionAsync_HappyPath_SendsPrompt()
    {
        _sessionRepo.GetByIdAsync("s1").Returns(new Session
        {
            Id = "s1", InstanceId = "inst-1", Title = "T", Status = "active",
            Directory = "/tmp", CreatedAt = "2026-01-01", RetentionStatus = "active"
        });
        _tracker.Register("inst-1", _harnessInstance);

        var result = await _sut.PromptSessionAsync("s1", "hello");

        result.IsSuccess.ShouldBeTrue();
        await _harnessInstance.Received(1).SendPromptAsync("hello", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromptSessionAsync_WhenArchived_ReturnsValidationFailure()
    {
        _sessionRepo.GetByIdAsync("s-archived").Returns(new Session
        {
            Id = "s-archived",
            InstanceId = "inst-1",
            Title = "Archived",
            Status = "stopped",
            RetentionStatus = "archived",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });

        var result = await _sut.PromptSessionAsync("s-archived", "hello");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.RetentionStatus");
        await _harnessInstance.DidNotReceive().SendPromptAsync(Arg.Any<string>(), Arg.Any<PromptOptions?>(), Arg.Any<CancellationToken>());
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.Count.ShouldBe(1);
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.Count.ShouldBe(3);
    }

    [Fact]
    public async Task GetSessionMessages_NoInstance_NoDbMessages_ReturnsEmptyPage()
    {
        var session = MakeSession("s1", "inst-missing");
        _sessionRepo.GetByIdAsync("s1").Returns(session);
        _messageRepo.GetBySessionAsync("s1", Arg.Any<int>(), null)
            .Returns(new List<Domain.Entities.PersistedMessage>());

        var result = await _sut.GetSessionMessagesAsync("s1");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.ShouldBeEmpty();
        result.Value.HasMore.ShouldBeFalse();
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetSessionMessages_SessionNotFound_ReturnsNotFound()
    {
        _sessionRepo.GetByIdAsync("ghost").Returns((Session?)null);

        var result = await _sut.GetSessionMessagesAsync("ghost");

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("Session");
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

        result.IsSuccess.ShouldBeTrue();
        result.Value.HasMore.ShouldBeTrue();
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
        capturedQuery.ShouldNotBeNull();
        capturedQuery!.Limit.ShouldBe(10);
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
        using var tempDirectory = new TempDirectory();

        // Act
        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path,
            HarnessType = "claude-code"
        });

        // Assert — session stored with correct harness type
        result.IsSuccess.ShouldBeTrue();
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
        using var tempDirectory = new TempDirectory();

        // Act
        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path
            // HarnessType omitted → should default to "opencode"
        });

        // Assert — default harness type is opencode
        result.IsSuccess.ShouldBeTrue();
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
        result.IsSuccess.ShouldBeTrue();
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
            RetentionStatus = "active",
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
        result.IsSuccess.ShouldBeTrue();
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
            RetentionStatus = "active",
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
        result.IsSuccess.ShouldBeTrue();
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
            RetentionStatus = "active",
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
        result.IsSuccess.ShouldBeTrue();
        await _harness.Received(1).SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>());
        await _harness.DidNotReceive().ResumeAsync(Arg.Any<HarnessResumeOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenArchived_ReturnsValidationFailure()
    {
        var session = new Session
        {
            Id = "s-archived-resume",
            InstanceId = "inst-old",
            WorkspaceId = "ws-1",
            HarnessType = "opencode",
            Title = "Archived",
            Status = "stopped",
            RetentionStatus = "archived",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        };
        _sessionRepo.GetByIdAsync("s-archived-resume").Returns(session);

        var result = await _sut.ResumeSessionAsync("s-archived-resume");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.RetentionStatus");
        _harnessRegistry.DidNotReceive().GetByType(Arg.Any<string>());
    }

    [Fact]
    public async Task ForkSessionAsync_InheritsParentHarnessType()
    {
        using var tempDirectory = new TempDirectory();

        // Arrange — parent has "claude-code"
        var parent = new Session
        {
            Id = "s-parent",
            InstanceId = "inst-parent",
            HarnessType = "claude-code",
            Title = "Parent",
            Status = "active",
            Directory = tempDirectory.Path,
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
        result.IsSuccess.ShouldBeTrue();
        await _sessionRepo.Received(1).InsertAsync(Arg.Is<Session>(s =>
            s.HarnessType == "claude-code"));
    }

    [Fact]
    public async Task EnsureDelegatedChildSessionAsync_CreatesHiddenChildSession()
    {
        var parent = new Session
        {
            Id = "parent-1",
            InstanceId = "inst-parent",
            WorkspaceId = "ws-1",
            HarnessType = "opencode",
            ProjectId = "proj-1",
            Title = "Parent",
            Status = "active",
            Directory = "/tmp/parent",
            CreatedAt = "2026-01-01"
        };

        _sessionRepo.GetByIdAsync("parent-1").Returns(parent);
        _sessionRepo.GetByHarnessIdAsync("oc-child-1").Returns((Session?)null);
        _projectRepo.ListAsync().Returns([
            new Project { Id = "proj-1", Name = "Project One", Type = "user", Position = 0, CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" },
        ]);
        _harnessRegistry.GetByType("opencode").Returns(_harness);
        _harness.Capabilities.Returns(new HarnessCapabilities { SupportsResume = true });

        var childInstance = Substitute.For<IHarnessInstance>();
        childInstance.InstanceId.Returns("inst-child");
        childInstance.HarnessType.Returns("opencode");
        childInstance.Status.Returns(HarnessInstanceStatus.Running);
        _harness.ResumeAsync(Arg.Any<HarnessResumeOptions>(), Arg.Any<CancellationToken>())
            .Returns(childInstance);
        _instanceRepo.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        _sessionRepo.InsertAsync(Arg.Any<Session>()).Returns(Task.CompletedTask);

        var result = await _sut.EnsureDelegatedChildSessionAsync("parent-1", "oc-child-1", "thread");

        result.IsSuccess.ShouldBeTrue();
        result.Value.IsHidden.ShouldBeTrue();
        result.Value.ParentSessionId.ShouldBe("parent-1");
        result.Value.HarnessResumeToken.ShouldBe("oc-child-1");
        result.Value.OpencodeSessionId.ShouldBe("oc-child-1");
        result.Value.UserId.ShouldBe("user-1");

        await _harness.Received(1).ResumeAsync(
            Arg.Is<HarnessResumeOptions>(o => o.ResumeToken == "oc-child-1" && o.SessionId == result.Value.Id),
            Arg.Any<CancellationToken>());
        await _sessionRepo.Received(1).InsertAsync(Arg.Is<Session>(s =>
            s.IsHidden &&
            s.ParentSessionId == "parent-1" &&
            s.InstanceId == "inst-child" &&
            s.HarnessResumeToken == "oc-child-1" &&
            s.UserId == "user-1"));
    }

    [Fact]
    public async Task DeleteSessionAsync_WhenSessionIsDelegationChild_CompletesDelegationBeforeDelete()
    {
        var session = new Session
        {
            Id = "child-1",
            InstanceId = "inst-1",
            WorkspaceId = "ws-1",
            Title = "Child",
            Status = "completed",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        };
        var delegation = new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ChildSessionId = "child-1",
            ParentToolCallId = "tool-1",
            Title = "reviewer",
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        };

        _sessionRepo.GetByIdAsync("child-1").Returns(session);
        _delegationRepo.GetByChildSessionIdAsync("child-1").Returns(delegation);
        _delegationRepo.GetByIdAsync("del-1").Returns(delegation);
        _sessionRepo.DeleteAsync("child-1").Returns(true);
        _harnessInstance.DeleteAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _tracker.Register("inst-1", _harnessInstance);

        var result = await _sut.DeleteSessionAsync("child-1");

        result.IsSuccess.ShouldBeTrue();
        Received.InOrder(async () =>
        {
            await _delegationRepo.GetByChildSessionIdAsync("child-1");
            await _delegationRepo.GetByIdAsync("del-1");
            await _delegationRepo.UpdateStatusAsync(
                Arg.Is("del-1"),
                Arg.Is("completed"),
                Arg.Any<string>(),
                Arg.Any<string>());
            await _sessionRepo.DeleteAsync("child-1");
        });
        await _harnessInstance.Received(1).DeleteAsync(Arg.Any<CancellationToken>());
        await _eventBroadcaster.Received(1).BroadcastAsync("sessions", "session_deleted", Arg.Any<object>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopSessionAsync_WhenRunning_StopsInstanceAndBroadcastsStopped()
    {
        _sessionRepo.GetByIdAsync("s-stop").Returns(new Session
        {
            Id = "s-stop",
            InstanceId = "inst-1",
            Title = "Stop",
            Status = "active",
            RetentionStatus = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });
        _tracker.Register("inst-1", _harnessInstance);

        var result = await _sut.StopSessionAsync("s-stop");

        result.IsSuccess.ShouldBeTrue();
        await _harnessInstance.Received(1).StopAsync(Arg.Any<CancellationToken>());
        await _sessionRepo.Received(1).UpdateStatusAsync("s-stop", "stopped", Arg.Any<string>());
        await _eventBroadcaster.Received(1).BroadcastAsync("sessions", "session_stopped", Arg.Any<object>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ArchiveSessionAsync_WhenActive_ArchivesAndBroadcasts()
    {
        _sessionRepo.GetByIdAsync("s-archive").Returns(new Session
        {
            Id = "s-archive",
            InstanceId = "inst-1",
            Title = "Archive",
            Status = "stopped",
            RetentionStatus = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });

        var result = await _sut.ArchiveSessionAsync("s-archive");

        result.IsSuccess.ShouldBeTrue();
        await _sessionRepo.Received(1).ArchiveAsync("s-archive", Arg.Any<string>());
        await _eventBroadcaster.Received(1).BroadcastAsync("sessions", "session_archived", Arg.Any<object>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnarchiveSessionAsync_WhenArchived_UnarchivesAndBroadcasts()
    {
        _sessionRepo.GetByIdAsync("s-unarchive").Returns(new Session
        {
            Id = "s-unarchive",
            InstanceId = "inst-1",
            Title = "Unarchive",
            Status = "stopped",
            RetentionStatus = "archived",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });

        var result = await _sut.UnarchiveSessionAsync("s-unarchive");

        result.IsSuccess.ShouldBeTrue();
        await _sessionRepo.Received(1).UnarchiveAsync("s-unarchive");
        await _eventBroadcaster.Received(1).BroadcastAsync("sessions", "session_unarchived", Arg.Any<object>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weave-fleet-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    /// <summary>Minimal stub for RuntimeLaunchArtifacts (opaque pass-through in tests).</summary>
    private sealed record StubLaunchArtifacts : RuntimeLaunchArtifacts;
}
