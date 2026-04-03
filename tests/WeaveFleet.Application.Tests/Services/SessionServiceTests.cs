using NSubstitute;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class SessionServiceTests
{
    private readonly ISessionRepository _sessionRepo = Substitute.For<ISessionRepository>();
    private readonly IProjectRepository _projectRepo = Substitute.For<IProjectRepository>();
    private readonly SessionService _sut;

    public SessionServiceTests()
    {
        _sut = new SessionService(_sessionRepo, _projectRepo);
    }

    [Fact]
    public async Task GetSessionAsync_WhenExists_ReturnsSession()
    {
        var session = MakeSession("s1");
        _sessionRepo.GetByIdAsync("s1").Returns(session);

        var result = await _sut.GetSessionAsync("s1");

        Assert.True(result.IsSuccess);
        Assert.Equal("s1", result.Value.Id);
    }

    [Fact]
    public async Task GetSessionAsync_WhenMissing_ReturnsFailure()
    {
        _sessionRepo.GetByIdAsync("missing").Returns((Session?)null);

        var result = await _sut.GetSessionAsync("missing");

        Assert.True(result.IsFailure);
        Assert.Contains("NotFound", result.Error.Code);
    }

    [Fact]
    public async Task DeleteSessionAsync_WhenExists_ReturnsTrue()
    {
        _sessionRepo.DeleteAsync("s1").Returns(true);

        var result = await _sut.DeleteSessionAsync("s1");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task DeleteSessionAsync_WhenMissing_ReturnsFailure()
    {
        _sessionRepo.DeleteAsync("missing").Returns(false);

        var result = await _sut.DeleteSessionAsync("missing");

        Assert.True(result.IsFailure);
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

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task GetFleetSummaryAsync_ReturnsAggregatedData()
    {
        _sessionRepo.GetStatusCountsAsync().Returns((Active: 3, Idle: 1));
        _sessionRepo.GetFleetTokenTotalsAsync().Returns((TotalTokens: 1000, TotalCost: 0.50));

        var result = await _sut.GetFleetSummaryAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.ActiveSessions);
        Assert.Equal(1, result.Value.IdleSessions);
        Assert.Equal(1000, result.Value.TotalTokens);
        Assert.Equal(0.50, result.Value.TotalCost, 5);
    }

    private static Session MakeSession(string id) => new()
    {
        Id = id,
        WorkspaceId = "w1",
        InstanceId = "i1",
        OpencodeSessionId = $"oc-{id}",
        Title = "Test",
        Status = "active",
        Directory = "/tmp",
        CreatedAt = "2026-01-01T00:00:00.0000000Z"
    };
}
