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

public sealed class MultiTenancyTests
{
    [Fact]
    public async Task CreateWorkspaceAsync_AssignsCurrentUserId()
    {
        var workspaceRepository = Substitute.For<IWorkspaceRepository>();
        var userContext = new TestUserContext("owner-1");
        var service = new WorkspaceService(
            workspaceRepository,
            userContext,
            new FleetOptions(),
            NullLogger<WorkspaceService>.Instance);

        using var tempDirectory = new TempDirectory();

        var result = await service.CreateWorkspaceAsync(tempDirectory.Path);

        result.IsSuccess.ShouldBeTrue();
        result.Value.UserId.ShouldBe("owner-1");
        await workspaceRepository.Received(1).InsertAsync(Arg.Is<Workspace>(workspace =>
            workspace.Directory == tempDirectory.Path &&
            workspace.UserId == "owner-1"));
    }

    [Fact]
    public async Task CreateSessionAsync_BroadcastsEventsToOwningUserAndPersistsOwner()
    {
        var harnessRegistry = Substitute.For<IHarnessRegistry>();
        var harness = Substitute.For<IHarness>();
        var harnessInstance = Substitute.For<IHarnessInstance>();
        var sessionRepository = Substitute.For<ISessionRepository>();
        var sessionSourceUsageRepository = Substitute.For<ISessionSourceUsageRepository>();
        var sessionCallbackRepository = Substitute.For<ISessionCallbackRepository>();
        var delegationRepository = Substitute.For<IDelegationRepository>();
        var projectRepository = Substitute.For<IProjectRepository>();
        var workspaceRepository = Substitute.For<IWorkspaceRepository>();
        var workspaceRootRepository = Substitute.For<IWorkspaceRootRepository>();
        var instanceRepository = Substitute.For<IInstanceRepository>();
        var eventBroadcaster = Substitute.For<IEventBroadcaster>();
        var analyticsCollector = Substitute.For<IAnalyticsCollector>();
        var messageRepository = Substitute.For<IMessageRepository>();
        var credentialStore = Substitute.For<ICredentialStore>();
        var userContext = new TestUserContext("owner-1");

        workspaceRootRepository.ListAsync().Returns([
            new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") }
        ]);

        harnessRegistry.GetByType("opencode").Returns(harness);
        harnessInstance.InstanceId.Returns("inst-1");
        harnessInstance.HarnessType.Returns("opencode");
        harnessInstance.Status.Returns(HarnessInstanceStatus.Running);
        harness.SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>()).Returns(harnessInstance);
        harness.PrepareRuntimeAsync(Arg.Any<RuntimePreparationContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new StubLaunchArtifacts())));
        credentialStore.GetDecryptedCredentialsAsync(Arg.Any<string>()).Returns([]);
        projectRepository.ListAsync().Returns([
            new Project
            {
                Id = "scratch-1",
                Name = "Scratch",
                Type = "scratch",
                Position = 0,
                CreatedAt = "2026-01-01",
                UpdatedAt = "2026-01-01",
                UserId = "owner-1"
            }
        ]);
        instanceRepository.GetByIdAsync(Arg.Any<string>()).Returns(call => new Instance
        {
            Id = call.Arg<string>(),
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = "owner-1"
        });

        var workspaceRootService = new WorkspaceRootService(workspaceRootRepository, userContext);
        var options = new FleetOptions();
        var workspaceService = new WorkspaceService(workspaceRepository, userContext, options, NullLogger<WorkspaceService>.Instance);
        var instanceService = new InstanceService(instanceRepository, sessionRepository, userContext);
        var sourceResolutionService = new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService)
        ]);
        var delegationService = new DelegationService(delegationRepository, eventBroadcaster, userContext);
        var orchestrator = new SessionOrchestrator(
            workspaceService,
            instanceService,
            sourceResolutionService,
            harnessRegistry,
            new InstanceTracker(),
            sessionRepository,
            sessionSourceUsageRepository,
            sessionCallbackRepository,
            delegationRepository,
            projectRepository,
            eventBroadcaster,
            analyticsCollector,
            messageRepository,
            delegationService,
            credentialStore,
            userContext,
            options,
            NullLogger<SessionOrchestrator>.Instance);

        using var tempDirectory = new TempDirectory();

        var result = await orchestrator.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path,
            Title = "Owned Session"
        });

        result.IsSuccess.ShouldBeTrue();
        result.Value.Session.UserId.ShouldBe("owner-1");
        await sessionRepository.Received(1).InsertAsync(Arg.Is<Session>(session =>
            session.Title == "Owned Session" &&
            session.UserId == "owner-1"));
        await eventBroadcaster.Received(1).BroadcastAsync(
            "sessions",
            "session_created",
            Arg.Any<object>(),
            "owner-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateSessionAsync_WhenCompletionCallbackTargetsDifferentUser_ReturnsUnauthorized()
    {
        var harnessRegistry = Substitute.For<IHarnessRegistry>();
        var harness = Substitute.For<IHarness>();
        var harnessInstance = Substitute.For<IHarnessInstance>();
        var sessionRepository = Substitute.For<ISessionRepository>();
        var sessionSourceUsageRepository = Substitute.For<ISessionSourceUsageRepository>();
        var sessionCallbackRepository = Substitute.For<ISessionCallbackRepository>();
        var delegationRepository = Substitute.For<IDelegationRepository>();
        var projectRepository = Substitute.For<IProjectRepository>();
        var workspaceRepository = Substitute.For<IWorkspaceRepository>();
        var workspaceRootRepository = Substitute.For<IWorkspaceRootRepository>();
        var instanceRepository = Substitute.For<IInstanceRepository>();
        var eventBroadcaster = Substitute.For<IEventBroadcaster>();
        var analyticsCollector = Substitute.For<IAnalyticsCollector>();
        var messageRepository = Substitute.For<IMessageRepository>();
        var credentialStore = Substitute.For<ICredentialStore>();
        var userContext = new TestUserContext("owner-1");

        workspaceRootRepository.ListAsync().Returns([
            new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") }
        ]);

        harnessRegistry.GetByType("opencode").Returns(harness);
        harnessInstance.InstanceId.Returns("inst-1");
        harnessInstance.HarnessType.Returns("opencode");
        harnessInstance.Status.Returns(HarnessInstanceStatus.Running);
        harness.SpawnAsync(Arg.Any<HarnessSpawnOptions>(), Arg.Any<CancellationToken>()).Returns(harnessInstance);
        harness.PrepareRuntimeAsync(Arg.Any<RuntimePreparationContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new StubLaunchArtifacts())));
        credentialStore.GetDecryptedCredentialsAsync(Arg.Any<string>()).Returns([]);
        projectRepository.ListAsync().Returns([
            new Project
            {
                Id = "scratch-1",
                Name = "Scratch",
                Type = "scratch",
                Position = 0,
                CreatedAt = "2026-01-01",
                UpdatedAt = "2026-01-01",
                UserId = "owner-1"
            }
        ]);
        instanceRepository.GetByIdAsync(Arg.Any<string>()).Returns(call => new Instance
        {
            Id = call.Arg<string>(),
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = "owner-1"
        });
        sessionRepository.GetByIdAsync("target-session").Returns(new Session
        {
            Id = "target-session",
            InstanceId = "inst-target",
            WorkspaceId = "ws-target",
            Title = "Other User Session",
            Status = "active",
            Directory = "/tmp/other",
            CreatedAt = "2026-01-01",
            UserId = "other-user"
        });

        var workspaceRootService = new WorkspaceRootService(workspaceRootRepository, userContext);
        var options = new FleetOptions();
        var workspaceService = new WorkspaceService(workspaceRepository, userContext, options, NullLogger<WorkspaceService>.Instance);
        var instanceService = new InstanceService(instanceRepository, sessionRepository, userContext);
        var sourceResolutionService = new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService)
        ]);
        var delegationService = new DelegationService(delegationRepository, eventBroadcaster, userContext);
        var orchestrator = new SessionOrchestrator(
            workspaceService,
            instanceService,
            sourceResolutionService,
            harnessRegistry,
            new InstanceTracker(),
            sessionRepository,
            sessionSourceUsageRepository,
            sessionCallbackRepository,
            delegationRepository,
            projectRepository,
            eventBroadcaster,
            analyticsCollector,
            messageRepository,
            delegationService,
            credentialStore,
            userContext,
            options,
            NullLogger<SessionOrchestrator>.Instance);

        using var tempDirectory = new TempDirectory();

        var result = await orchestrator.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path,
            OnCompleteTargetSessionId = "target-session",
            OnCompleteTargetInstanceId = "inst-target"
        });

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("General.Unauthorized");
        await sessionCallbackRepository.DidNotReceive().InsertAsync(Arg.Any<SessionCallback>());
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weave-fleet-multitenancy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed record StubLaunchArtifacts : RuntimeLaunchArtifacts;
}
