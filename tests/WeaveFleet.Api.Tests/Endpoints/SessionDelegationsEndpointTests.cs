using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class SessionDelegationsEndpointTests
{
    [Fact]
    public async Task GetSessionDelegations_WhenSessionExists_ReturnsOkWithDelegations()
    {
        var sessionRepo = new InMemorySessionRepository();
        sessionRepo.Seed(new Session
        {
            Id = "session-1",
            WorkspaceId = "ws-1",
            InstanceId = "inst-1",
            OpencodeSessionId = "oc-1",
            Title = "Parent",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var sessionService = new SessionService(
            sessionRepo,
            new InMemoryProjectRepository(),
            new SessionOrchestratorBuilder().Build());

        var delegationRepo = new InMemoryDelegationRepository();
        delegationRepo.Seed(new Delegation
        {
            Id = "del-1",
            ParentSessionId = "session-1",
            ParentToolCallId = "tool-1",
            ChildSessionId = "child-1",
            Title = "reviewer",
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });
        var delegationService = new DelegationService(
            delegationRepo,
            new FakeEventBroadcaster(),
            new TestUserContext("user-1"));

        var result = await InvokeGetSessionDelegations("session-1", sessionService, delegationService);

        var ok = result.ShouldBeOfType<Ok<IReadOnlyList<DelegationDto>>>();
        ok.Value.ShouldNotBeNull();
        ok.Value.Count.ShouldBe(1);
        ok.Value[0].DelegationId.ShouldBe("del-1");
    }

    [Fact]
    public async Task GetSessionDelegations_WhenSessionMissing_ReturnsNotFound()
    {
        var sessionService = new SessionService(
            new InMemorySessionRepository(),
            new InMemoryProjectRepository(),
            new SessionOrchestratorBuilder().Build());

        var delegationService = new DelegationService(
            new InMemoryDelegationRepository(),
            new FakeEventBroadcaster(),
            new TestUserContext("user-1"));

        var result = await InvokeGetSessionDelegations("missing", sessionService, delegationService);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> InvokeGetSessionDelegations(
        string id,
        SessionService sessionService,
        DelegationService delegationService)
    {
        var sessionResult = await sessionService.GetSessionAsync(id);
        return await sessionResult.Match<Task<IResult>>(
            async _ => TypedResults.Ok<IReadOnlyList<DelegationDto>>(await delegationService.GetDelegationsAsync(id)),
            error => Task.FromResult(ToApiResult(error)));
    }

    private static IResult ToApiResult(WeaveFleet.Domain.Common.FleetError error) =>
        error.Code switch
        {
            var code when code.EndsWith(".NotFound", StringComparison.Ordinal) => TypedResults.NotFound(new { error = error.Description }),
            "General.Conflict" => TypedResults.Conflict(new { error = error.Description }),
            var code when code.StartsWith("Validation.", StringComparison.Ordinal) => TypedResults.BadRequest(new { error = error.Description }),
            _ => TypedResults.Problem(error.Description)
        };
}
