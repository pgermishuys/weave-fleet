using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class SessionOrchestratorCredentialTests
{
    private readonly IHarnessRegistry _harnessRegistry = Substitute.For<IHarnessRegistry>();
    private readonly IHarness _harness = Substitute.For<IHarness>();
    private readonly IHarnessRuntime _harnessRuntime = Substitute.For<IHarnessRuntime>();
    private readonly IHarnessSession _harnessInstance = Substitute.For<IHarnessSession>();
    private readonly ISessionRepository _sessionRepository = Substitute.For<ISessionRepository>();
    private readonly ISessionSourceUsageRepository _sessionSourceUsageRepository = Substitute.For<ISessionSourceUsageRepository>();
    private readonly ISessionCallbackRepository _sessionCallbackRepository = Substitute.For<ISessionCallbackRepository>();
    private readonly IDelegationRepository _delegationRepository = Substitute.For<IDelegationRepository>();
    private readonly IProjectRepository _projectRepository = Substitute.For<IProjectRepository>();
    private readonly IWorkspaceRepository _workspaceRepository = Substitute.For<IWorkspaceRepository>();
    private readonly IWorkspaceRootRepository _workspaceRootRepository = Substitute.For<IWorkspaceRootRepository>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IEventBroadcaster _eventBroadcaster = Substitute.For<IEventBroadcaster>();
    private readonly IAnalyticsCollector _analyticsCollector = Substitute.For<IAnalyticsCollector>();
    private readonly IMessageRepository _messageRepository = Substitute.For<IMessageRepository>();
    private readonly ICredentialStore _credentialStore = Substitute.For<ICredentialStore>();
    private readonly FleetOptions _options = new();
    private readonly IUserContext _userContext = new TestUserContext("participant-1");
    private readonly SessionOrchestrator _sut;

    public SessionOrchestratorCredentialTests()
    {
        _workspaceRootRepository.ListAsync().Returns([
            new WorkspaceRoot
            {
                Id = "root-1",
                Path = Path.GetTempPath(),
                CreatedAt = DateTime.UtcNow.ToString("O")
            }
        ]);

        var workspaceRootService = new WorkspaceRootService(_workspaceRootRepository, _userContext);
        var workspaceService = new WorkspaceService(
            _workspaceRepository,
            _userContext,
            _options,
            NullLogger<WorkspaceService>.Instance);
        var instanceService = new InstanceService(_instanceRepository, _sessionRepository, _userContext);
        var sessionSourceResolutionService = new SessionSourceResolutionService(
            [
                new LocalDirectorySessionSourceProvider(workspaceRootService),
                new ManagedWorkspaceSessionSourceProvider(_options)
            ],
            _options);
        var delegationService = new DelegationService(_delegationRepository, _eventBroadcaster, _userContext);

        _credentialStore.GetDecryptedCredentialsAsync(Arg.Any<string>()).Returns([]);
        _harnessRegistry.GetByType("opencode").Returns(_harness);
        _harnessRegistry.GetRuntimeByType("opencode").Returns(_harnessRuntime);
        _harnessInstance.InstanceId.Returns("inst-1");
        _harnessInstance.HarnessType.Returns("opencode");
        _harnessInstance.Status.Returns(HarnessSessionStatus.Running);
        _harness.Capabilities.Returns(new HarnessCapabilities { SupportsResume = true });
        _harnessRuntime.SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>()).Returns(_harnessInstance);
        _harnessRuntime.ResumeAsync(Arg.Any<HarnessResumeOptions>(), Arg.Any<CancellationToken>()).Returns(_harnessInstance);
        _projectRepository.ListAsync().Returns([
            new Project
            {
                Id = "scratch-1",
                Name = "Scratch",
                Type = "scratch",
                Position = 0,
                CreatedAt = "2026-01-01",
                UpdatedAt = "2026-01-01"
            }
        ]);
        _instanceRepository.InsertAsync(Arg.Any<Instance>()).Returns(Task.CompletedTask);
        _sessionRepository.InsertAsync(Arg.Any<Session>()).Returns(Task.CompletedTask);
        _sessionRepository.UpdateForResumeAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);

        _sut = new SessionOrchestrator(
            workspaceService,
            instanceService,
            sessionSourceResolutionService,
            _harnessRegistry,
            new InstanceTracker(),
            _sessionRepository,
            _sessionSourceUsageRepository,
            _sessionCallbackRepository,
            _delegationRepository,
            _projectRepository,
            _eventBroadcaster,
            _analyticsCollector,
            _messageRepository,
            delegationService,
            _credentialStore,
            _userContext,
            _options,
            NullLogger<SessionOrchestrator>.Instance);
    }

    [Fact]
    public async Task CreateSessionAsync_LoadsCurrentUserCredentialsAndPassesThemToPrepareRuntimeAsync()
    {
        using var tempDirectory = new TempDirectory();
        var credentials = new List<UserCredential>
        {
            CreateCredential("participant-1", "anthropic", "api-key", "secret-1")
        };
        RuntimePreparationContext? capturedContext = null;

        _credentialStore.GetDecryptedCredentialsAsync("participant-1").Returns(credentials);
        _harnessRuntime.PrepareRuntimeAsync(Arg.Do<RuntimePreparationContext>(context => capturedContext = context), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new StubLaunchArtifacts())));

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path,
            Title = "Credentialed Session"
        });

        result.IsSuccess.ShouldBeTrue();
        await _credentialStore.Received(1).GetDecryptedCredentialsAsync("participant-1");
        capturedContext.ShouldNotBeNull();
        capturedContext.UserId.ShouldBe("participant-1");
        capturedContext.UserCredentials.ShouldBeSameAs(credentials);
        capturedContext.ModelId.ShouldBeNull();
        capturedContext.WorkingDirectory.ShouldBe(WorkspaceRootService.CanonicalizePath(tempDirectory.Path));
    }

    [Fact]
    public async Task CreateSessionAsync_WhenPrepareRuntimeReturnsNotReady_ReturnsValidationFailureWithoutSpawning()
    {
        using var tempDirectory = new TempDirectory();

        _harnessRuntime.PrepareRuntimeAsync(Arg.Any<RuntimePreparationContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RuntimePreparation>(
                new RuntimePreparation.NotReady([
                    new RuntimePreparationError(
                        "MissingCredential",
                        "An Anthropic API key is required to use this model.",
                        "Add an API key in Settings → Credentials")
                ])));

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path
        });

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.NotReady");
        result.Error.Description.ShouldBe("An Anthropic API key is required to use this model.");
        await _harnessRuntime.DidNotReceive().SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>());
        await _sessionRepository.DidNotReceive().InsertAsync(Arg.Any<Session>());
    }

    [Fact]
    public async Task CreateSessionAsync_WhenPrepareRuntimeReturnsReady_PassesArtifactsThroughToSpawnAsync()
    {
        using var tempDirectory = new TempDirectory();
        var artifacts = new StubLaunchArtifacts();

        _harnessRuntime.PrepareRuntimeAsync(Arg.Any<RuntimePreparationContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(artifacts)));

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path
        });

        result.IsSuccess.ShouldBeTrue();
        await _harnessRuntime.Received(1).SpawnAsync(
            Arg.Is<HarnessSpawnOptions>(options => ReferenceEquals(options.LaunchArtifacts, artifacts)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeSessionAsync_LoadsOwnerCredentialsAndPassesArtifactsToResumeAsync()
    {
        var ownerCredentials = new List<UserCredential>
        {
            CreateCredential("owner-1", "anthropic", "api-key", "owner-secret")
        };
        var artifacts = new StubLaunchArtifacts();
        RuntimePreparationContext? capturedContext = null;

        _sessionRepository.GetByIdAsync("session-1").Returns(new Session
        {
            Id = "session-1",
            WorkspaceId = "workspace-1",
            InstanceId = "inst-old",
            HarnessType = "opencode",
            HarnessResumeToken = "resume-token-1",
            Title = "Resume Me",
            Status = "active",
            RetentionStatus = "active",
            Directory = "/tmp/workspace",
            CreatedAt = "2026-01-01",
            UserId = "owner-1"
        });
        _workspaceRepository.GetByIdAsync("workspace-1").Returns(new Workspace
        {
            Id = "workspace-1",
            Directory = "/tmp/workspace",
            CreatedAt = "2026-01-01",
            UserId = "owner-1"
        });
        _credentialStore.GetDecryptedCredentialsAsync("owner-1").Returns(ownerCredentials);
        _harnessRuntime.PrepareRuntimeAsync(Arg.Do<RuntimePreparationContext>(context => capturedContext = context), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(artifacts)));

        var result = await _sut.ResumeSessionAsync("session-1");

        result.IsSuccess.ShouldBeTrue();
        await _credentialStore.Received(1).GetDecryptedCredentialsAsync("owner-1");
        capturedContext.ShouldNotBeNull();
        capturedContext.UserId.ShouldBe("owner-1");
        capturedContext.UserCredentials.ShouldBeSameAs(ownerCredentials);
        capturedContext.WorkingDirectory.ShouldBe("/tmp/workspace");
        await _harnessRuntime.Received(1).ResumeAsync(
            Arg.Is<HarnessResumeOptions>(options =>
                options.OwnerUserId == "owner-1" &&
                options.ResumeToken == "resume-token-1" &&
                ReferenceEquals(options.LaunchArtifacts, artifacts)),
            Arg.Any<CancellationToken>());
        await _harnessRuntime.DidNotReceive().SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenPrepareRuntimeReturnsNotReady_DoesNotResumeOrSpawn()
    {
        _sessionRepository.GetByIdAsync("session-2").Returns(new Session
        {
            Id = "session-2",
            WorkspaceId = "workspace-2",
            InstanceId = "inst-old",
            HarnessType = "opencode",
            HarnessResumeToken = "resume-token-2",
            Title = "Blocked Resume",
            Status = "active",
            RetentionStatus = "active",
            Directory = "/tmp/workspace-2",
            CreatedAt = "2026-01-01",
            UserId = "owner-2"
        });
        _workspaceRepository.GetByIdAsync("workspace-2").Returns(new Workspace
        {
            Id = "workspace-2",
            Directory = "/tmp/workspace-2",
            CreatedAt = "2026-01-01",
            UserId = "owner-2"
        });
        _harnessRuntime.PrepareRuntimeAsync(Arg.Any<RuntimePreparationContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RuntimePreparation>(
                new RuntimePreparation.NotReady([
                    new RuntimePreparationError("MissingCredential", "Add credentials before resuming.")
                ])));

        var result = await _sut.ResumeSessionAsync("session-2");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.NotReady");
        result.Error.Description.ShouldBe("Add credentials before resuming.");
        await _harnessRuntime.DidNotReceive().ResumeAsync(Arg.Any<HarnessResumeOptions>(), Arg.Any<CancellationToken>());
        await _harnessRuntime.DidNotReceive().SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenResumeIsUnsupported_PassesArtifactsThroughToSpawnAsync()
    {
        var artifacts = new StubLaunchArtifacts();

        _sessionRepository.GetByIdAsync("session-3").Returns(new Session
        {
            Id = "session-3",
            WorkspaceId = "workspace-3",
            InstanceId = "inst-old",
            HarnessType = "opencode",
            HarnessResumeToken = "resume-token-3",
            Title = "Fallback Resume",
            Status = "active",
            RetentionStatus = "active",
            Directory = "/tmp/workspace-3",
            CreatedAt = "2026-01-01",
            UserId = "owner-3"
        });
        _workspaceRepository.GetByIdAsync("workspace-3").Returns(new Workspace
        {
            Id = "workspace-3",
            Directory = "/tmp/workspace-3",
            CreatedAt = "2026-01-01",
            UserId = "owner-3"
        });
        _harness.Capabilities.Returns(new HarnessCapabilities { SupportsResume = false });
        _harnessRuntime.PrepareRuntimeAsync(Arg.Any<RuntimePreparationContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(artifacts)));

        var result = await _sut.ResumeSessionAsync("session-3");

        result.IsSuccess.ShouldBeTrue();
        await _harnessRuntime.Received(1).SpawnAsync(
            Arg.Is<HarnessSpawnOptions>(options =>
                options.OwnerUserId == "owner-3" &&
                ReferenceEquals(options.LaunchArtifacts, artifacts)),
            Arg.Any<CancellationToken>());
        await _harnessRuntime.DidNotReceive().ResumeAsync(Arg.Any<HarnessResumeOptions>(), Arg.Any<CancellationToken>());
    }

    private static UserCredential CreateCredential(string userId, string credentialNamespace, string kind, string decryptedValue)
    {
        var timestamp = DateTime.UtcNow.ToString("O");
        return new UserCredential
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            Namespace = credentialNamespace,
            Kind = kind,
            Label = $"{credentialNamespace}-{kind}-{Guid.NewGuid():N}",
            EncryptedValue = decryptedValue,
            DisplayHint = decryptedValue.Length >= 4 ? decryptedValue[^4..] : decryptedValue,
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weave-fleet-credentials-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed record StubLaunchArtifacts : RuntimeLaunchArtifacts;
}

