using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
}
