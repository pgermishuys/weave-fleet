using NSubstitute;
using Shouldly;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class SessionServiceTests
{
    private readonly ISessionRepository _sessionRepo = Substitute.For<ISessionRepository>();
    private readonly ISessionSourceUsageRepository _sessionSourceUsageRepo = Substitute.For<ISessionSourceUsageRepository>();
    private readonly ISessionCallbackRepository _callbackRepo = Substitute.For<ISessionCallbackRepository>();
    private readonly IDelegationRepository _delegationRepo = Substitute.For<IDelegationRepository>();
    private readonly IProjectRepository _projectRepo = Substitute.For<IProjectRepository>();
    private readonly IWorkspaceRepository _workspaceRepo = Substitute.For<IWorkspaceRepository>();
    private readonly IWorkspaceRootRepository _workspaceRootRepo = Substitute.For<IWorkspaceRootRepository>();
    private readonly IInstanceRepository _instanceRepo = Substitute.For<IInstanceRepository>();
    private readonly IEventBroadcaster _eventBroadcaster = Substitute.For<IEventBroadcaster>();
    private readonly WeaveFleet.Application.Analytics.IAnalyticsCollector _analyticsCollector =
        Substitute.For<WeaveFleet.Application.Analytics.IAnalyticsCollector>();
    private readonly IMessageRepository _messageRepo = Substitute.For<IMessageRepository>();
    private readonly SessionOrchestrator _sessionOrchestrator;
    private readonly SessionService _sut;

    public SessionServiceTests()
    {
        _workspaceRootRepo.ListAsync().Returns([
            new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") }
        ]);
        var workspaceRootService = new WorkspaceRootService(_workspaceRootRepo);
        var workspaceService = new WorkspaceService(
            _workspaceRepo,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceService>.Instance);
        var instanceService = new InstanceService(_instanceRepo, _sessionRepo);
        var sessionSourceResolutionService = new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService)
        ]);
        var delegationService = new DelegationService(_delegationRepo, _eventBroadcaster);
        _sessionOrchestrator = new SessionOrchestrator(
            workspaceService,
            instanceService,
            sessionSourceResolutionService,
            Substitute.For<WeaveFleet.Application.Harnesses.IHarnessRegistry>(),
            new InstanceTracker(),
            _sessionRepo,
            _sessionSourceUsageRepo,
            _callbackRepo,
            _delegationRepo,
            _projectRepo,
            _eventBroadcaster,
            _analyticsCollector,
            _messageRepo,
            delegationService,
            new WeaveFleet.Application.Configuration.FleetOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionOrchestrator>.Instance);
        _sut = new SessionService(_sessionRepo, _projectRepo, _sessionOrchestrator);
        _instanceRepo.GetByIdAsync(Arg.Any<string>()).Returns(callInfo => new Instance
        {
            Id = callInfo.Arg<string>(),
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });
        _sessionRepo.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(Task.CompletedTask);
        _sessionRepo.ArchiveAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        _sessionRepo.UnarchiveAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task GetSessionAsync_WhenExists_ReturnsSession()
    {
        var session = MakeSession("s1");
        _sessionRepo.GetByIdAsync("s1").Returns(session);

        var result = await _sut.GetSessionAsync("s1");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Id.ShouldBe("s1");
    }

    [Fact]
    public async Task GetSessionAsync_WhenMissing_ReturnsFailure()
    {
        _sessionRepo.GetByIdAsync("missing").Returns((Session?)null);

        var result = await _sut.GetSessionAsync("missing");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldContain("NotFound");
    }

    [Fact]
    public async Task DeleteSessionAsync_WhenExists_ReturnsTrue()
    {
        _sessionRepo.GetByIdAsync("s1").Returns(MakeSession("s1"));
        _delegationRepo.GetByChildSessionIdAsync("s1").Returns((Delegation?)null);
        _sessionRepo.DeleteAsync("s1").Returns(true);
        _instanceRepo.UpdateStatusAsync("i1", "stopped", Arg.Any<string>()).Returns(Task.CompletedTask);

        var result = await _sut.DeleteSessionAsync("s1");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task StopSessionAsync_WhenOrchestratorSucceeds_ReturnsSuccess()
    {
        _sessionRepo.GetByIdAsync("s-stop").Returns(MakeSession("s-stop"));
        _instanceRepo.UpdateStatusAsync("i1", "stopped", Arg.Any<string>()).Returns(Task.CompletedTask);

        var result = await _sut.StopSessionAsync("s-stop");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateRetentionAsync_WhenArchived_ArchivesSession()
    {
        _sessionRepo.GetByIdAsync("s1").Returns(new Session
        {
            Id = "s1",
            WorkspaceId = "w1",
            InstanceId = "i1",
            OpencodeSessionId = "oc-s1",
            Title = "Test",
            Status = "stopped",
            RetentionStatus = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01T00:00:00.0000000Z"
        });

        var result = await _sut.UpdateRetentionAsync("s1", "archived");

        result.IsSuccess.ShouldBeTrue();
        await _sessionRepo.Received(1).ArchiveAsync("s1", Arg.Any<string>());
    }

    [Fact]
    public async Task UpdateRetentionAsync_WhenUnsupportedStatus_ReturnsValidationFailure()
    {
        var result = await _sut.UpdateRetentionAsync("s1", "deleted");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.RetentionStatus");
    }

    [Fact]
    public async Task ListSessionsAsync_WhenRetentionFilterMissing_DefaultsToActive()
    {
        _sessionRepo.ListAsync(
            10,
            5,
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>?>())
            .Returns([MakeSession("s1")]);

        var result = await _sut.ListSessionsAsync(10, 5, null, null, null);

        result.IsSuccess.ShouldBeTrue();
        await _sessionRepo.Received(1).ListAsync(
            10,
            5,
            Arg.Is<IReadOnlyList<string>?>(value => value == null),
            Arg.Is<string?>(value => value == null),
            Arg.Is<IReadOnlyList<string>?>(value => value != null && value.Count == 1 && value[0] == "active"));
    }

    [Fact]
    public async Task ListSessionsAsync_WhenRetentionFilterAll_OmitsRetentionConstraint()
    {
        _sessionRepo.ListAsync(
            10,
            0,
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>?>())
            .Returns([MakeSession("s1")]);

        var result = await _sut.ListSessionsAsync(10, 0, null, null, "all");

        result.IsSuccess.ShouldBeTrue();
        await _sessionRepo.Received(1).ListAsync(
            10,
            0,
            Arg.Is<IReadOnlyList<string>?>(value => value == null),
            Arg.Is<string?>(value => value == null),
            Arg.Is<IReadOnlyList<string>?>(value => value == null));
    }

    [Fact]
    public async Task DeleteSessionAsync_WhenMissing_ReturnsFailure()
    {
        _sessionRepo.GetByIdAsync("missing").Returns((Session?)null);

        var result = await _sut.DeleteSessionAsync("missing");

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task MoveSessionToProjectAsync_ValidProject_ReturnsSuccess()
    {
        _sessionRepo.GetByIdAsync("s1").Returns(MakeSession("s1"));
        _projectRepo.GetByIdAsync("p1").Returns(new Project
        {
            Id = "p1", Name = "P1", Type = "user", Position = 1,
            CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01"
        });
        _sessionRepo.UpdateProjectAsync("s1", "p1").Returns(Task.CompletedTask);

        var result = await _sut.MoveSessionToProjectAsync("s1", "p1");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task GetFleetSummaryAsync_ReturnsAggregatedData()
    {
        _sessionRepo.GetStatusCountsAsync().Returns((Active: 3, Idle: 1));
        _sessionRepo.GetFleetTokenTotalsAsync().Returns((TotalTokens: 1000, TotalCost: 0.50));

        var result = await _sut.GetFleetSummaryAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.ActiveSessions.ShouldBe(3);
        result.Value.IdleSessions.ShouldBe(1);
        result.Value.TotalTokens.ShouldBe(1000);
        result.Value.TotalCost.ShouldBe(0.50, 0.00001);
    }

    private static Session MakeSession(string id) => new()
    {
        Id = id,
        WorkspaceId = "w1",
        InstanceId = "i1",
        OpencodeSessionId = $"oc-{id}",
        Title = "Test",
        Status = "active",
        RetentionStatus = "active",
        Directory = "/tmp",
        CreatedAt = "2026-01-01T00:00:00.0000000Z"
    };
}
