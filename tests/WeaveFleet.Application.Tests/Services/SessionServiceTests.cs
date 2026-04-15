using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class SessionServiceTests
{
    private readonly SessionOrchestratorBuilder _builder;
    private readonly SessionService _sut;

    public SessionServiceTests()
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

        var orchestrator = _builder.Build();
        _sut = new SessionService(_builder.SessionRepository, _builder.ProjectRepository, orchestrator);
    }

    [Fact]
    public async Task GetSessionAsync_WhenExists_ReturnsSession()
    {
        _builder.SessionRepository.Seed(MakeSession("s1"));

        var result = await _sut.GetSessionAsync("s1");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Id.ShouldBe("s1");
    }

    [Fact]
    public async Task GetSessionAsync_WhenMissing_ReturnsFailure()
    {
        var result = await _sut.GetSessionAsync("missing");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldContain("NotFound");
    }

    [Fact]
    public async Task DeleteSessionAsync_WhenExists_ReturnsTrue()
    {
        _builder.SessionRepository.Seed(MakeSession("s1"));

        var result = await _sut.DeleteSessionAsync("s1");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task StopSessionAsync_WhenOrchestratorSucceeds_ReturnsSuccess()
    {
        _builder.SessionRepository.Seed(MakeSession("s-stop"));

        var result = await _sut.StopSessionAsync("s-stop");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateRetentionAsync_WhenArchived_ArchivesSession()
    {
        _builder.SessionRepository.Seed(new Session
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
        _builder.SessionRepository.ArchiveCalls.Count.ShouldBe(1);
        _builder.SessionRepository.ArchiveCalls[0].Id.ShouldBe("s1");
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
        _builder.SessionRepository.Seed(MakeSession("s1"));

        var result = await _sut.ListSessionsAsync(10, 5, null, null, null);

        result.IsSuccess.ShouldBeTrue();
        _builder.SessionRepository.ListAsyncCalls.Count.ShouldBe(1);
        var call = _builder.SessionRepository.ListAsyncCalls[0];
        call.Limit.ShouldBe(10);
        call.Offset.ShouldBe(5);
        call.Statuses.ShouldBeNull();
        call.ProjectId.ShouldBeNull();
        call.RetentionStatuses.ShouldNotBeNull();
        call.RetentionStatuses!.Count.ShouldBe(1);
        call.RetentionStatuses![0].ShouldBe("active");
    }

    [Fact]
    public async Task ListSessionsAsync_WhenRetentionFilterAll_OmitsRetentionConstraint()
    {
        _builder.SessionRepository.Seed(MakeSession("s1"));

        var result = await _sut.ListSessionsAsync(10, 0, null, null, "all");

        result.IsSuccess.ShouldBeTrue();
        _builder.SessionRepository.ListAsyncCalls.Count.ShouldBe(1);
        var call = _builder.SessionRepository.ListAsyncCalls[0];
        call.Limit.ShouldBe(10);
        call.Offset.ShouldBe(0);
        call.Statuses.ShouldBeNull();
        call.ProjectId.ShouldBeNull();
        call.RetentionStatuses.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteSessionAsync_WhenMissing_ReturnsFailure()
    {
        var result = await _sut.DeleteSessionAsync("missing");

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task MoveSessionToProjectAsync_ValidProject_ReturnsSuccess()
    {
        _builder.SessionRepository.Seed(MakeSession("s1"));
        _builder.ProjectRepository.Seed(new Project
        {
            Id = "p1", Name = "P1", Type = "user", Position = 1,
            CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01"
        });

        var result = await _sut.MoveSessionToProjectAsync("s1", "p1");

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task GetFleetSummaryAsync_ReturnsAggregatedData()
    {
        // Seed 3 active + 1 idle sessions with tokens/cost totalling 1000 / $0.50
        for (var i = 1; i <= 3; i++)
            _builder.SessionRepository.Seed(new Session
            {
                Id = $"active-{i}", WorkspaceId = "w1", InstanceId = "i1",
                OpencodeSessionId = $"oc-active-{i}", Title = "Active", Status = "active",
                Directory = "/tmp", CreatedAt = "2026-01-01",
                TotalTokens = 300, TotalCost = 0.15
            });
        _builder.SessionRepository.Seed(new Session
        {
            Id = "idle-1", WorkspaceId = "w1", InstanceId = "i1",
            OpencodeSessionId = "oc-idle-1", Title = "Idle", Status = "idle",
            Directory = "/tmp", CreatedAt = "2026-01-01",
            TotalTokens = 100, TotalCost = 0.05
        });

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
