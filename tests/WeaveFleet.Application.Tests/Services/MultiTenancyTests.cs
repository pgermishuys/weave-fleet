using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class MultiTenancyTests
{
    [Fact]
    public async Task CreateWorkspaceAsync_AssignsCurrentUserId()
    {
        var workspaceRepository = new InMemoryWorkspaceRepository();
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
        workspaceRepository.InsertedWorkspaces.ShouldContain(w =>
            w.Directory == tempDirectory.Path &&
            w.UserId == "owner-1");
    }

    [Fact]
    public async Task CreateSessionAsync_BroadcastsEventsToOwningUserAndPersistsOwner()
    {
        var userContext = new TestUserContext("owner-1");
        var builder = new SessionOrchestratorBuilder()
            .WithUserContext(userContext);

        builder.WorkspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = Path.GetTempPath(),
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var runtime = builder.RegisterHarness("opencode", "OpenCode");
        runtime.PrepareRuntimeBehavior = (_, _) =>
            Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new StubLaunchArtifacts()));
        var session = new FakeHarnessSession("inst-1") { HarnessType = "opencode", Status = HarnessSessionStatus.Running };
        runtime.SpawnBehavior = (_, _) => Task.FromResult<IHarnessSession>(session);

        builder.ProjectRepository.Seed(new Project
        {
            Id = "scratch-1",
            Name = "Scratch",
            Type = "scratch",
            Position = 0,
            CreatedAt = "2026-01-01",
            UpdatedAt = "2026-01-01",
            UserId = "owner-1"
        });

        builder.InstanceRepository.GetByIdBehavior = id => Task.FromResult<Instance?>(new Instance
        {
            Id = id,
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = "owner-1"
        });

        var orchestrator = builder.Build();

        using var tempDirectory = new TempDirectory();

        var result = await orchestrator.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path,
            Title = "Owned Session"
        });

        result.IsSuccess.ShouldBeTrue();
        result.Value.Session.UserId.ShouldBe("owner-1");
        builder.SessionRepository.All.ShouldContain(s =>
            s.Title == "Owned Session" &&
            s.UserId == "owner-1");
        builder.EventBroadcaster.Broadcasts.ShouldContain(b =>
            b.Topic == "sessions" &&
            b.Type == "session_created" &&
            b.UserId == "owner-1");
    }

    [Fact]
    public async Task CreateSessionAsync_WhenCompletionCallbackTargetsDifferentUser_ReturnsUnauthorized()
    {
        var userContext = new TestUserContext("owner-1");
        var builder = new SessionOrchestratorBuilder()
            .WithUserContext(userContext);

        builder.WorkspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1",
            Path = Path.GetTempPath(),
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var runtime = builder.RegisterHarness("opencode", "OpenCode");
        runtime.PrepareRuntimeBehavior = (_, _) =>
            Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new StubLaunchArtifacts()));
        var session = new FakeHarnessSession("inst-1") { HarnessType = "opencode", Status = HarnessSessionStatus.Running };
        runtime.SpawnBehavior = (_, _) => Task.FromResult<IHarnessSession>(session);

        builder.ProjectRepository.Seed(new Project
        {
            Id = "scratch-1",
            Name = "Scratch",
            Type = "scratch",
            Position = 0,
            CreatedAt = "2026-01-01",
            UpdatedAt = "2026-01-01",
            UserId = "owner-1"
        });

        builder.InstanceRepository.GetByIdBehavior = id => Task.FromResult<Instance?>(new Instance
        {
            Id = id,
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = "owner-1"
        });

        builder.SessionRepository.Seed(new Session
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

        var orchestrator = builder.Build();

        using var tempDirectory = new TempDirectory();

        var result = await orchestrator.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path,
            OnCompleteTargetSessionId = "target-session",
            OnCompleteTargetInstanceId = "inst-target"
        });

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("General.Unauthorized");
        builder.SessionCallbackRepository.All.ShouldBeEmpty();
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
