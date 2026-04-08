using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class SessionDelegationsEndpointTests
{
    [Fact]
    public async Task GetSessionDelegations_WhenSessionExists_ReturnsOkWithDelegations()
    {
        var sessionService = new SessionService(
            BuildSessionRepository(new Session
            {
                Id = "session-1",
                WorkspaceId = "ws-1",
                InstanceId = "inst-1",
                OpencodeSessionId = "oc-1",
                Title = "Parent",
                Status = "active",
                Directory = "/tmp",
                CreatedAt = DateTime.UtcNow.ToString("O")
            }),
            Substitute.For<IProjectRepository>(),
            BuildSessionOrchestrator());

        var delegationRepository = Substitute.For<IDelegationRepository>();
        delegationRepository.GetByParentSessionIdAsync("session-1").Returns([
            new Delegation
            {
                Id = "del-1",
                ParentSessionId = "session-1",
                ParentToolCallId = "tool-1",
                ChildSessionId = "child-1",
                Title = "reviewer",
                Status = "running",
                CreatedAt = DateTime.UtcNow.ToString("O"),
                UpdatedAt = DateTime.UtcNow.ToString("O")
            }
        ]);
        var delegationService = new DelegationService(
            delegationRepository,
            Substitute.For<IEventBroadcaster>());

        var result = await InvokeGetSessionDelegations("session-1", sessionService, delegationService);

        var ok = Assert.IsType<Ok<IReadOnlyList<DelegationDto>>>(result);
        Assert.NotNull(ok.Value);
        Assert.Single(ok.Value);
        Assert.Equal("del-1", ok.Value[0].DelegationId);
    }

    [Fact]
    public async Task GetSessionDelegations_WhenSessionMissing_ReturnsNotFound()
    {
        var sessionService = new SessionService(
            BuildSessionRepository(null),
            Substitute.For<IProjectRepository>(),
            BuildSessionOrchestrator());

        var delegationService = new DelegationService(
            Substitute.For<IDelegationRepository>(),
            Substitute.For<IEventBroadcaster>());

        var result = await InvokeGetSessionDelegations("missing", sessionService, delegationService);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, ((IStatusCodeHttpResult)result).StatusCode);
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

    private static ISessionRepository BuildSessionRepository(Session? session)
    {
        var repository = Substitute.For<ISessionRepository>();
        repository.GetByIdAsync(Arg.Any<string>()).Returns(session);
        return repository;
    }

    private static SessionOrchestrator BuildSessionOrchestrator()
    {
        var sessionRepository = Substitute.For<ISessionRepository>();
        var workspaceRepository = Substitute.For<IWorkspaceRepository>();
        var instanceRepository = Substitute.For<IInstanceRepository>();
        var projectRepository = Substitute.For<IProjectRepository>();
        var callbackRepository = Substitute.For<ISessionCallbackRepository>();
        var delegationRepository = Substitute.For<IDelegationRepository>();
        var eventBroadcaster = Substitute.For<IEventBroadcaster>();
        var analyticsCollector = Substitute.For<WeaveFleet.Application.Analytics.IAnalyticsCollector>();
        var messageRepository = Substitute.For<IMessageRepository>();

        return new SessionOrchestrator(
            new WorkspaceService(
                workspaceRepository,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceService>.Instance),
            new InstanceService(instanceRepository, sessionRepository),
            Substitute.For<WeaveFleet.Application.Harnesses.IHarnessRegistry>(),
            new InstanceTracker(),
            sessionRepository,
            callbackRepository,
            delegationRepository,
            projectRepository,
            eventBroadcaster,
            analyticsCollector,
            messageRepository,
            new DelegationService(delegationRepository, eventBroadcaster),
            new WeaveFleet.Application.Configuration.FleetOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionOrchestrator>.Instance);
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
