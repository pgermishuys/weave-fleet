using System.Text.Json;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class SessionOrchestratorTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder;
    private readonly InstanceTracker _tracker = new();
    private readonly FakeHarnessSession _defaultSession = new("inst-1");
    private readonly SessionOrchestrator _sut;

    public ValueTask DisposeAsync() => _defaultSession.DisposeAsync();

    public SessionOrchestratorTests()
    {
        _builder = new SessionOrchestratorBuilder()
            .WithUserContext(new TestUserContext("user-1"));

        _builder.WorkspaceRootRepository.Seed(
            new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") }
        );

        // Dynamic instance lookup: return a running instance for any id
        _builder.InstanceRepository.GetByIdBehavior = id => Task.FromResult<Instance?>(new Instance
        {
            Id = id,
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        _sut = _builder.Build();
    }

    private void ConfigureHarnessAndScratchProject(string harnessType = "opencode")
    {
        var runtime = _builder.RegisterHarness(harnessType, "OpenCode");
        runtime.DefaultSession = _defaultSession;
        _builder.ProjectRepository.Seed(new Project
        {
            Id = "scratch-1", Name = "Scratch", Type = "scratch", Position = 0,
            CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01"
        });
    }

    // ── CreateSessionAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSessionAsync_WhenHarnessNotFound_ReturnsFailure()
    {
        // No harness registered → GetByType returns null
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
        _builder.SessionRepository.InsertedSessions
            .ShouldContain(s => s.Title == "My Session" && s.ProjectId == "scratch-1" && s.UserId == "user-1");
    }

    [Fact]
    public async Task CreateSessionAsync_WithHybridSource_IncludesContextInInitialPrompt()
    {
        ConfigureHarnessAndScratchProject();
        using var tempDirectory = new TempDirectory();

        var sut = BuildSutWithTracker(new StubSessionSourceProvider(
            new SessionSourceDescriptor(
                new SessionSourceKey
                {
                    ProviderId = SessionSourceProviderIds.GitHub,
                    SourceType = SessionSourceTypeNames.GitHubIssue,
                    ActionId = SessionSourceActions.StartSession,
                    ContractVersion = 1
                },
                "GitHub issue",
                SessionSourceKinds.Hybrid,
                [],
                ProducesWorkspace: true,
                ProducesContext: true,
                RequiresConfirmation: false),
            new ResolvedSessionSource(
                new SessionSourceDescriptor(
                    new SessionSourceKey
                    {
                        ProviderId = SessionSourceProviderIds.GitHub,
                        SourceType = SessionSourceTypeNames.GitHubIssue,
                        ActionId = SessionSourceActions.StartSession,
                        ContractVersion = 1
                    },
                    "GitHub issue",
                    SessionSourceKinds.Hybrid,
                    [],
                    ProducesWorkspace: true,
                    ProducesContext: true,
                    RequiresConfirmation: false),
                new ResolvedSessionInput(
                    new WorkspaceIntent(tempDirectory.Path, "existing", null),
                    new ContextEnvelope("GitHub issue #42", "Issue context body", false, 18),
                    new ProvenanceRecord(
                        SessionSourceProviderIds.GitHub,
                        SessionSourceTypeNames.GitHubIssue,
                        SessionSourceActions.StartSession,
                        "acme/rocket#42",
                        "https://github.com/acme/rocket/issues/42",
                        "GitHub issue",
                        null,
                        DateTime.UtcNow.ToString("O"))))));

        var result = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Source = new SessionSourceSelection
            {
                Key = new SessionSourceKey
                {
                    ProviderId = SessionSourceProviderIds.GitHub,
                    SourceType = SessionSourceTypeNames.GitHubIssue,
                    ActionId = SessionSourceActions.StartSession,
                    ContractVersion = 1
                },
                Input = JsonSerializer.SerializeToElement(new
                {
                    owner = "acme",
                    repo = "rocket",
                    number = 42,
                    repositoryPath = tempDirectory.Path
                })
            }
        });

        result.IsSuccess.ShouldBeTrue();
        var runtime = _builder.HarnessRegistry.GetRuntimeByType("opencode").ShouldBeOfType<FakeHarnessRuntime>();
        runtime.SpawnCalls.Any(call => call.InitialPrompt is not null && call.InitialPrompt.Contains("[Source: GitHub issue #42]", StringComparison.Ordinal)).ShouldBeTrue();
        runtime.SpawnCalls.Any(call => call.InitialPrompt is not null && call.InitialPrompt.Contains("Issue context body", StringComparison.Ordinal)).ShouldBeTrue();
        _builder.SessionSourceUsageRepository.All.ShouldContain(usage =>
            usage.ProviderId == SessionSourceProviderIds.GitHub
            && usage.ActionId == SessionSourceActions.StartSession);
    }

    [Fact]
    public async Task CreateSessionAsync_WhenSpawnThrows_ReturnsUnexpectedError()
    {
        var runtime = _builder.RegisterHarness("opencode", "OpenCode");
        runtime.SpawnBehavior = (_, _) => throw new InvalidOperationException("process failed");
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
        using var workspaceRoot = new TempDirectory();
        var builder = new SessionOrchestratorBuilder()
            .WithUserContext(new TestUserContext("user-1"))
            .WithOptions(new FleetOptions
            {
                Cloud = new CloudOptions { Enabled = true, WorkspaceRoot = workspaceRoot.Path }
            });
        builder.WorkspaceRootRepository.Seed(
            new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") }
        );
        var runtime = builder.RegisterHarness("opencode", "OpenCode");
        runtime.DefaultSession = new FakeHarnessSession("inst-1");
        builder.ProjectRepository.Seed(new Project
        {
            Id = "scratch-1", Name = "Scratch", Type = "scratch", Position = 0,
            CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01"
        });
        var sut = builder.Build();

        var result = await sut.CreateSessionAsync(new CreateSessionRequest());

        result.IsSuccess.ShouldBeTrue();
        builder.SessionRepository.InsertedSessions
            .ShouldContain(s => s.Directory.StartsWith(workspaceRoot.Path, StringComparison.OrdinalIgnoreCase));
        builder.SessionSourceUsageRepository.All
            .ShouldContain(u => u.ProviderId == SessionSourceProviderIds.Managed &&
                                u.SourceType == SessionSourceTypeNames.ManagedWorkspace);
    }

    [Fact]
    public async Task PreviewAddSourceToSessionAsync_WhenSourceResolves_ReturnsEnvelope()
    {
        var sessionId = "session-1";
        _builder.SessionRepository.Seed(new Session
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
        _builder.SessionRepository.Seed(new Session
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
        _builder.SessionSourceUsageRepository.All.ShouldBeEmpty();
    }

    // ── PromptSessionAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task PromptSessionAsync_WhenSessionNotFound_ReturnsFailure()
    {
        var result = await _sut.PromptSessionAsync("missing", "hello");

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("Session");
    }

    [Fact]
    public async Task PromptSessionAsync_WhenInstanceNotTracked_ReturnsFailure()
    {
        _builder.SessionRepository.Seed(new Session
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
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1", InstanceId = "inst-1", Title = "T", Status = "active",
            Directory = "/tmp", CreatedAt = "2026-01-01", RetentionStatus = "active"
        });
        _tracker.Register("inst-1", _defaultSession);
        // Re-build sut with the tracker that has the session registered
        var sut = BuildSutWithTracker();

        var result = await sut.PromptSessionAsync("s1", "hello");

        result.IsSuccess.ShouldBeTrue();
        _defaultSession.SendPromptCalls.Count.ShouldBe(1);
        _defaultSession.SendPromptCalls[0].Text.ShouldBe("hello");
    }

    [Fact]
    public async Task PromptSessionAsync_WithExplicitProviderAndModel_SendsStructuredPromptOptions()
    {
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1", InstanceId = "inst-1", Title = "T", Status = "active",
            Directory = "/tmp", CreatedAt = "2026-01-01", RetentionStatus = "active"
        });
        _tracker.Register("inst-1", _defaultSession);
        var sut = BuildSutWithTracker();

        var result = await sut.PromptSessionAsync("s1", "hello", new PromptOptions
        {
            ProviderId = "openrouter",
            ModelId = "anthropic/claude-sonnet-4"
        });

        result.IsSuccess.ShouldBeTrue();
        _defaultSession.SendPromptCalls.Count.ShouldBe(1);
        var promptOptions = _defaultSession.SendPromptCalls[0].Options;
        promptOptions.ShouldNotBeNull();
        promptOptions.ProviderId.ShouldBe("openrouter");
        promptOptions.ModelId.ShouldBe("anthropic/claude-sonnet-4");
    }

    [Fact]
    public async Task CommandSessionAsync_WithUnqualifiedLegacyModel_KeepsProviderNull()
    {
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1", InstanceId = "inst-1", Title = "T", Status = "active",
            Directory = "/tmp", CreatedAt = "2026-01-01", RetentionStatus = "active"
        });
        _tracker.Register("inst-1", _defaultSession);
        var sut = BuildSutWithTracker();

        var result = await sut.CommandSessionAsync("s1", new CommandOptions
        {
            Command = "start-work",
            ProviderId = null,
            ModelId = "claude-sonnet-4"
        });

        result.IsSuccess.ShouldBeTrue();
        _defaultSession.SendCommandCalls.Count.ShouldBe(1);
        _defaultSession.SendCommandCalls[0].ProviderId.ShouldBeNull();
        _defaultSession.SendCommandCalls[0].ModelId.ShouldBe("claude-sonnet-4");
    }

    [Fact]
    public async Task PromptSessionAsync_WhenArchived_ReturnsValidationFailure()
    {
        _builder.SessionRepository.Seed(new Session
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
        _defaultSession.SendPromptCalls.ShouldBeEmpty();
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
    public async Task GetSessionMessages_LiveInstance_StillReadsFromDb()
    {
        var session = MakeSession("s1", "inst-1");
        _builder.SessionRepository.Seed(session);
        _tracker.Register("inst-1", _defaultSession);
        foreach (var m in MakePersistedMessages("s1", 1))
            _builder.MessageRepository.Seed(m);
        var sut = BuildSutWithTracker();

        var result = await sut.GetSessionMessagesAsync("s1");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.Count.ShouldBe(1);
        _defaultSession.GetMessagesBehavior.ShouldBeNull(); // never configured → never called
    }

    [Fact]
    public async Task GetSessionMessages_NoInstance_FallsBackToDb()
    {
        var session = MakeSession("s1", "inst-missing");
        _builder.SessionRepository.Seed(session);
        foreach (var m in MakePersistedMessages("s1", 3))
            _builder.MessageRepository.Seed(m);

        var result = await _sut.GetSessionMessagesAsync("s1");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.Count.ShouldBe(3);
    }

    [Fact]
    public async Task GetSessionMessages_NoInstance_NoDbMessages_ReturnsEmptyPage()
    {
        var session = MakeSession("s1", "inst-missing");
        _builder.SessionRepository.Seed(session);

        var result = await _sut.GetSessionMessagesAsync("s1");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.ShouldBeEmpty();
        result.Value.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task GetSessionMessages_LiveInstanceIsIgnoredEvenIfItWouldThrow()
    {
        var session = MakeSession("s1", "inst-1");
        _builder.SessionRepository.Seed(session);
        _tracker.Register("inst-1", _defaultSession);
        // Configure GetMessages to throw — but it should never be called
        _defaultSession.GetMessagesBehavior = (_, _) => throw new HttpRequestException("connection refused");
        foreach (var m in MakePersistedMessages("s1", 2))
            _builder.MessageRepository.Seed(m);
        var sut = BuildSutWithTracker();

        var result = await sut.GetSessionMessagesAsync("s1");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetSessionMessages_SessionNotFound_ReturnsNotFound()
    {
        var result = await _sut.GetSessionMessagesAsync("ghost");

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("Session");
    }

    [Fact]
    public async Task GetSessionMessages_UsesHistoryPageSizeForDbFallback()
    {
        var session = MakeSession("s2", "inst-missing");
        _builder.SessionRepository.Seed(session);

        // Return limit+1 rows to trigger hasMore
        _builder.MessageRepository.GetBySessionBehavior = (sessionId, limit, _) =>
            Task.FromResult<IReadOnlyList<PersistedMessage>>(MakePersistedMessages(sessionId, limit + 1));

        var result = await _sut.GetSessionMessagesAsync("s2");

        result.IsSuccess.ShouldBeTrue();
        result.Value.HasMore.ShouldBeTrue();
        // DB was called with HistoryMessagePageSize+1 (default 10 → fetches 11)
        _builder.MessageRepository.GetBySessionCalls.ShouldContain(c => c.SessionId == "s2" && c.Limit == 11);
    }

    [Fact]
    public async Task GetSessionMessages_WhenHasMore_KeepsNewestMessagesInFetchedWindow()
    {
        var session = MakeSession("s2-tail", "inst-missing");
        _builder.SessionRepository.Seed(session);

        var rows = Enumerable.Range(0, 11)
            .Select(i => new Domain.Entities.PersistedMessage
            {
                Id = $"msg-{i}",
                SessionId = "s2-tail",
                Role = "assistant",
                PartsJson = $"[{{\"type\":\"text\",\"text\":\"message-{i}\"}}]",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(i).ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i).ToString("O"),
            })
            .ToList();

        _builder.MessageRepository.GetBySessionBehavior = (_, _, _) =>
            Task.FromResult<IReadOnlyList<PersistedMessage>>(rows);

        var result = await _sut.GetSessionMessagesAsync("s2-tail");

        result.IsSuccess.ShouldBeTrue();
        result.Value.HasMore.ShouldBeTrue();
        result.Value.Messages.Count.ShouldBe(10);
        result.Value.Messages[0].Id.ShouldBe("msg-1");
        result.Value.Messages[^1].Id.ShouldBe("msg-10");
    }

    [Fact]
    public async Task GetSessionMessages_UsesHistoryPageSizeEvenForLiveInstance()
    {
        var session = MakeSession("s3", "inst-1");
        _builder.SessionRepository.Seed(session);
        _tracker.Register("inst-1", _defaultSession);
        foreach (var m in MakePersistedMessages("s3", 2))
            _builder.MessageRepository.Seed(m);
        var sut = BuildSutWithTracker();

        await sut.GetSessionMessagesAsync("s3");

        _builder.MessageRepository.GetBySessionCalls.ShouldContain(c => c.SessionId == "s3" && c.Limit == 11);
    }

    [Fact]
    public async Task GetCommittedEvents_ReturnsOutboxRowsAsCommittedEvents()
    {
        var session = MakeSession("s-committed", "inst-1");
        _builder.SessionRepository.Seed(session);
        _builder.OutboxRepository.Seed(new OutboxMessage
        {
            Id = 6,
            Topic = "session:s-committed",
            Type = "message.updated",
            Payload = "{\"info\":{\"id\":\"msg-1\"}}",
            CreatedAt = "2026-01-01T00:00:00.0000000+00:00",
            AvailableAt = "2026-01-01T00:00:00.0000000+00:00",
            UserId = "user-1"
        });

        var result = await _sut.GetCommittedEventsAsync("s-committed", 5, 20);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(1);
        result.Value[0].SequenceNumber.ShouldBe(6);
        result.Value[0].Topic.ShouldBe("session:s-committed");
        result.Value[0].Type.ShouldBe("message.updated");
    }

    [Fact]
    public async Task GetCommittedEvents_SessionNotFound_ReturnsNotFound()
    {
        var result = await _sut.GetCommittedEventsAsync("ghost", 0, 10);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("Session");
    }

    // ── Harness type tracking ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateSessionAsync_StoresHarnessType()
    {
        var runtime = _builder.RegisterHarness("claude-code", "Claude Code");
        runtime.DefaultSession = _defaultSession;
        using var tempDirectory = new TempDirectory();

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path,
            HarnessType = "claude-code"
        });

        result.IsSuccess.ShouldBeTrue();
        _builder.SessionRepository.InsertedSessions.ShouldContain(s => s.HarnessType == "claude-code");
    }

    [Fact]
    public async Task CreateSessionAsync_DefaultsToOpenCode()
    {
        var runtime = _builder.RegisterHarness("opencode", "OpenCode");
        runtime.DefaultSession = _defaultSession;
        using var tempDirectory = new TempDirectory();

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path
        });

        result.IsSuccess.ShouldBeTrue();
        _builder.SessionRepository.InsertedSessions.ShouldContain(s => s.HarnessType == "opencode");
    }

    [Fact]
    public async Task ResumeSessionAsync_UsesStoredHarnessType()
    {
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
        _builder.SessionRepository.Seed(session);
        _builder.WorkspaceRepository.Seed(new Domain.Entities.Workspace
        {
            Id = "ws-1",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });
        var runtime = _builder.RegisterHarness("claude-code", "Claude Code", new HarnessCapabilities { SupportsResume = false });
        runtime.DefaultSession = _defaultSession;

        var result = await _sut.ResumeSessionAsync("s-resume");

        result.IsSuccess.ShouldBeTrue();
        _builder.HarnessRegistry.GetByTypeCalls.ShouldContain("claude-code");
        _builder.HarnessRegistry.GetByTypeCalls.ShouldNotContain("opencode");
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenTokenPresent_AndSupportsResume_CallsResumeAsync()
    {
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
        _builder.SessionRepository.Seed(session);
        _builder.WorkspaceRepository.Seed(new Domain.Entities.Workspace
        {
            Id = "ws-2",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });
        var runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        runtime.DefaultSession = _defaultSession;

        var result = await _sut.ResumeSessionAsync("s-resume-token");

        result.IsSuccess.ShouldBeTrue();
        runtime.ResumeCalls.Count.ShouldBe(1);
        runtime.ResumeCalls[0].ResumeToken.ShouldBe("existing-session-token");
        runtime.SpawnCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenTokenNull_FallsBackToSpawnAsync()
    {
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
        _builder.SessionRepository.Seed(session);
        _builder.WorkspaceRepository.Seed(new Domain.Entities.Workspace
        {
            Id = "ws-3",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });
        var runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        runtime.DefaultSession = _defaultSession;

        var result = await _sut.ResumeSessionAsync("s-resume-notok");

        result.IsSuccess.ShouldBeTrue();
        runtime.SpawnCalls.Count.ShouldBe(1);
        runtime.ResumeCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenTokenPresent_ButSupportsResumeFalse_FallsBackToSpawnAsync()
    {
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
        _builder.SessionRepository.Seed(session);
        _builder.WorkspaceRepository.Seed(new Domain.Entities.Workspace
        {
            Id = "ws-4",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });
        var runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = false });
        runtime.DefaultSession = _defaultSession;

        var result = await _sut.ResumeSessionAsync("s-resume-nosupp");

        result.IsSuccess.ShouldBeTrue();
        runtime.SpawnCalls.Count.ShouldBe(1);
        runtime.ResumeCalls.ShouldBeEmpty();
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
        _builder.SessionRepository.Seed(session);

        var result = await _sut.ResumeSessionAsync("s-archived-resume");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.RetentionStatus");
        _builder.HarnessRegistry.GetByTypeCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ForkSessionAsync_InheritsParentHarnessType()
    {
        using var tempDirectory = new TempDirectory();

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
        _builder.SessionRepository.Seed(parent);
        var runtime = _builder.RegisterHarness("claude-code", "Claude Code");
        runtime.DefaultSession = _defaultSession;

        var result = await _sut.ForkSessionAsync("s-parent", "Forked Session");

        result.IsSuccess.ShouldBeTrue();
        _builder.SessionRepository.InsertedSessions.ShouldContain(s => s.HarnessType == "claude-code");
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
        _builder.SessionRepository.Seed(parent);
        _builder.ProjectRepository.Seed(new Project
        {
            Id = "proj-1", Name = "Project One", Type = "user", Position = 0,
            CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01"
        });

        var childSession = new FakeHarnessSession("inst-child");
        var runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        runtime.ResumeBehavior = (_, _) => Task.FromResult<IHarnessSession>(childSession);

        var result = await _sut.EnsureDelegatedChildSessionAsync("parent-1", "oc-child-1", "thread");

        result.IsSuccess.ShouldBeTrue();
        result.Value.IsHidden.ShouldBeTrue();
        result.Value.ParentSessionId.ShouldBe("parent-1");
        result.Value.HarnessResumeToken.ShouldBe("oc-child-1");
        result.Value.OpencodeSessionId.ShouldBe("oc-child-1");
        result.Value.UserId.ShouldBe("user-1");

        runtime.ResumeCalls.Count.ShouldBe(1);
        runtime.ResumeCalls[0].ResumeToken.ShouldBe("oc-child-1");
        runtime.ResumeCalls[0].SessionId.ShouldBe(result.Value.Id);

        _builder.SessionRepository.InsertedSessions.ShouldContain(s =>
            s.IsHidden &&
            s.ParentSessionId == "parent-1" &&
            s.InstanceId == "inst-child" &&
            s.HarnessResumeToken == "oc-child-1" &&
            s.UserId == "user-1");
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
            CreatedAt = "2026-01-01",
            UserId = "user-1"
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

        _builder.SessionRepository.Seed(session);
        _builder.DelegationRepository.Seed(delegation);
        _tracker.Register("inst-1", _defaultSession);
        var sut = BuildSutWithTracker();

        var result = await sut.DeleteSessionAsync("child-1");

        result.IsSuccess.ShouldBeTrue();
        _builder.DelegationRepository.UpdateStatusCalls.ShouldContain(c => c.Id == "del-1" && c.Status == "completed");
        _builder.DelegationRepository.UpdateChildSessionIdCalls.ShouldContain(c => c.Id == "del-1" && c.ChildSessionId == null);
        _builder.SessionRepository.All.ShouldNotContain(s => s.Id == "child-1");
        _defaultSession.DeleteCalled.ShouldBeTrue();
        _builder.EventBroadcaster.Broadcasts.ShouldContain(b =>
            b.Topic == "session:parent-1" && b.Type == "delegation.updated");
        _builder.EventBroadcaster.Broadcasts.ShouldContain(b =>
            b.Topic == "sessions" && b.Type == "session_deleted");
    }

    [Fact]
    public async Task DeleteSessionAsync_WhenSessionIsDelegationParent_DeletesDelegationsBeforeDelete()
    {
        var session = new Session
        {
            Id = "parent-1",
            InstanceId = "inst-1",
            WorkspaceId = "ws-1",
            Title = "Parent",
            Status = "completed",
            Directory = "/tmp",
            CreatedAt = "2026-01-01",
            UserId = "user-1"
        };
        _builder.SessionRepository.Seed(session);
        _builder.DelegationRepository.Seed(new Delegation
        {
            Id = "del-1",
            ParentSessionId = "parent-1",
            ChildSessionId = "child-1",
            Title = "reviewer",
            Status = "completed",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });

        var result = await _sut.DeleteSessionAsync("parent-1");

        result.IsSuccess.ShouldBeTrue();
        _builder.DelegationRepository.DeleteByParentSessionIdCalls.ShouldContain("parent-1");
        _builder.SessionRepository.All.ShouldNotContain(s => s.Id == "parent-1");
    }

    [Fact]
    public async Task StopSessionAsync_WhenRunning_StopsInstanceAndBroadcastsStopped()
    {
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s-stop",
            InstanceId = "inst-1",
            Title = "Stop",
            Status = "active",
            RetentionStatus = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });
        _tracker.Register("inst-1", _defaultSession);
        var sut = BuildSutWithTracker();

        var result = await sut.StopSessionAsync("s-stop");

        result.IsSuccess.ShouldBeTrue();
        _defaultSession.StopCalled.ShouldBeTrue();
        _builder.SessionRepository.UpdateStatusCalls.ShouldContain(c => c.Id == "s-stop" && c.Status == "stopped");
        _builder.EventBroadcaster.Broadcasts.ShouldContain(b => b.Topic == "sessions" && b.Type == "session_stopped");
    }

    [Fact]
    public async Task ArchiveSessionAsync_WhenActive_ArchivesAndBroadcasts()
    {
        _builder.SessionRepository.Seed(new Session
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
        _builder.SessionRepository.ArchiveCalls.ShouldContain(c => c.Id == "s-archive");
        _builder.EventBroadcaster.Broadcasts.ShouldContain(b => b.Topic == "sessions" && b.Type == "session_archived");
    }

    [Fact]
    public async Task UnarchiveSessionAsync_WhenArchived_UnarchivesAndBroadcasts()
    {
        _builder.SessionRepository.Seed(new Session
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
        _builder.SessionRepository.UnarchiveCalls.ShouldContain("s-unarchive");
        _builder.EventBroadcaster.Broadcasts.ShouldContain(b => b.Topic == "sessions" && b.Type == "session_unarchived");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a new SessionOrchestrator using the same builder fakes but with the pre-configured tracker.
    /// Used for tests that need to pre-register instances in the tracker.
    /// </summary>
    private SessionOrchestrator BuildSutWithTracker(params ISessionSourceProvider[] additionalProviders)
    {
        var userContext = new TestUserContext("user-1");
        var options = new FleetOptions();
        var workspaceRootService = new WorkspaceRootService(_builder.WorkspaceRootRepository, userContext);
        var workspaceService = new WeaveFleet.Application.Services.WorkspaceService(
            _builder.WorkspaceRepository,
            userContext,
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WeaveFleet.Application.Services.WorkspaceService>.Instance);
        var instanceService = new InstanceService(_builder.InstanceRepository, _builder.SessionRepository, userContext);
        var sessionSourceResolutionService = new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService),
            new ManagedWorkspaceSessionSourceProvider(options),
            .. additionalProviders
        ], options);
        var delegationService = new DelegationService(_builder.DelegationRepository, _builder.EventBroadcaster, userContext);

        return new SessionOrchestrator(
            workspaceService,
            instanceService,
            sessionSourceResolutionService,
            _builder.HarnessRegistry,
            _tracker,
            _builder.SessionRepository,
            _builder.SessionSourceUsageRepository,
            _builder.SessionCallbackRepository,
            _builder.DelegationRepository,
            _builder.ProjectRepository,
            _builder.EventBroadcaster,
            _builder.AnalyticsCollector,
            _builder.MessageRepository,
            _builder.OutboxRepository,
            delegationService,
            _builder.CredentialStore,
            userContext,
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionOrchestrator>.Instance,
            sessionActivityWriteService: null);
    }

    private sealed class StubSessionSourceProvider(SessionSourceDescriptor descriptor, ResolvedSessionSource resolved) : ISessionSourceProvider
    {
        public string ProviderId => descriptor.Key.ProviderId;

        public IReadOnlyList<SessionSourceDescriptor> GetDescriptors() => [descriptor];

        public Task<Result<ResolvedSessionSource>> ResolveAsync(SessionSourceSelection selection, CancellationToken cancellationToken)
        {
            return Task.FromResult<Result<ResolvedSessionSource>>(resolved);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
