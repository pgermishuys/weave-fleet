using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class SessionLifecycleEndpointTests
{
    [Fact]
    public async Task StopSession_WhenSuccessful_ReturnsNoContent()
    {
        var service = BuildSessionService(MakeSession("session-1", "active", "active"));

        var result = await InvokeStopSession("session-1", service);

        result.ShouldBeOfType<NoContent>();
    }

    [Fact]
    public async Task UpdateRetention_WhenArchiving_ReturnsNoContent()
    {
        var service = BuildSessionService(MakeSession("session-1", "stopped", "active"));

        var result = await InvokeUpdateRetention("session-1", new UpdateSessionRetentionRequest("archived"), service);

        result.ShouldBeOfType<NoContent>();
    }

    [Fact]
    public async Task UpdateRetention_WhenUnarchiving_ReturnsBadRequest()
    {
        var service = BuildSessionService(MakeSession("session-1", "stopped", "archived"));

        var result = await InvokeUpdateRetention("session-1", new UpdateSessionRetentionRequest("active"), service);

        result.ShouldBeOfType<BadRequest<object>>();
    }

    [Fact]
    public async Task DeleteSession_WhenSuccessful_ReturnsNoContent()
    {
        var service = BuildSessionService(MakeSession("session-1", "stopped", "archived"));

        var result = await InvokeDeleteSession("session-1", service);

        result.ShouldBeOfType<NoContent>();
    }

    [Fact]
    public async Task DeleteSession_WhenMissing_ReturnsNotFound()
    {
        var service = BuildSessionService((Session?)null);

        var result = await InvokeDeleteSession("missing", service);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task ListSessions_WhenRetentionFilterMissing_UsesActiveRetention()
    {
        var sessionRepo = new InMemorySessionRepository();
        sessionRepo.Seed(MakeSession("session-1", "active", "active"));
        var service = BuildSessionService(sessionRepo);

        var result = await service.ListSessionsAsync(25, 5, null, null, null);

        result.IsSuccess.ShouldBeTrue();
        sessionRepo.ListAsyncCalls.Count.ShouldBe(1);
        var call = sessionRepo.ListAsyncCalls[0];
        call.Limit.ShouldBe(25);
        call.Offset.ShouldBe(5);
        call.Statuses.ShouldBeNull();
        call.ProjectId.ShouldBeNull();
        call.RetentionStatuses.ShouldNotBeNull();
        call.RetentionStatuses!.Count.ShouldBe(1);
        call.RetentionStatuses[0].ShouldBe("active");
    }

    [Fact]
    public async Task ListSessions_WhenRetentionFilterAll_OmitsRetentionConstraint()
    {
        var sessionRepo = new InMemorySessionRepository();
        sessionRepo.Seed(MakeSession("session-1", "active", "active"));
        var service = BuildSessionService(sessionRepo);

        var result = await service.ListSessionsAsync(25, 5, null, null, "all");

        result.IsSuccess.ShouldBeTrue();
        sessionRepo.ListAsyncCalls.Count.ShouldBe(1);
        var call = sessionRepo.ListAsyncCalls[0];
        call.Limit.ShouldBe(25);
        call.Offset.ShouldBe(5);
        call.Statuses.ShouldBeNull();
        call.ProjectId.ShouldBeNull();
        call.RetentionStatuses.ShouldBeNull();
    }

    private static async Task<IResult> InvokeStopSession(string id, SessionService sessionService)
    {
        var result = await sessionService.StopSessionAsync(id);
        return result.Match<IResult>(
            _ => TypedResults.NoContent(),
            error => ToApiResult(error));
    }

    private static async Task<IResult> InvokeUpdateRetention(string id, UpdateSessionRetentionRequest request, SessionService sessionService)
    {
        var result = await sessionService.UpdateRetentionAsync(id, request.RetentionStatus);
        return result.Match<IResult>(
            _ => TypedResults.NoContent(),
            error => ToApiResult(error));
    }

    private static async Task<IResult> InvokeDeleteSession(string id, SessionService sessionService)
    {
        var result = await sessionService.DeleteSessionAsync(id);
        return result.Match<IResult>(
            _ => TypedResults.NoContent(),
            error => ToApiResult(error));
    }

    private static SessionService BuildSessionService(Session? session)
    {
        var builder = new SessionOrchestratorBuilder();
        if (session is not null)
            builder.SessionRepository.Seed(session);
        builder.InstanceRepository.Seed(new Instance
        {
            Id = "inst-1",
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });
        var orchestrator = builder.Build();
        return new SessionService(builder.SessionRepository, builder.ProjectRepository, orchestrator);
    }

    private static SessionService BuildSessionService(InMemorySessionRepository sessionRepository)
    {
        var builder = new SessionOrchestratorBuilder();
        // Seed the builder's session repo with the same data
        foreach (var s in sessionRepository.All)
            builder.SessionRepository.Seed(s);
        builder.InstanceRepository.Seed(new Instance
        {
            Id = "inst-1",
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });
        var orchestrator = builder.Build();
        // Return service using the passed-in sessionRepository so ListAsyncCalls are tracked there
        return new SessionService(sessionRepository, builder.ProjectRepository, orchestrator);
    }

    private static Session MakeSession(string id, string status, string retentionStatus)
        => new()
        {
            Id = id,
            WorkspaceId = "ws-1",
            InstanceId = "inst-1",
            OpencodeSessionId = $"oc-{id}",
            Title = "Session",
            Status = status,
            RetentionStatus = retentionStatus,
            Directory = "/tmp",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

    private static IResult ToApiResult(WeaveFleet.Domain.Common.FleetError error) =>
        error.Code switch
        {
            var code when code.EndsWith(".NotFound", StringComparison.Ordinal) => TypedResults.NotFound(new { error = error.Description }),
            "General.Conflict" => TypedResults.Conflict(new { error = error.Description }),
            var code when code.StartsWith("Validation.", StringComparison.Ordinal) => TypedResults.BadRequest(new { error = error.Description }),
            _ => TypedResults.Problem(error.Description)
        };
}
