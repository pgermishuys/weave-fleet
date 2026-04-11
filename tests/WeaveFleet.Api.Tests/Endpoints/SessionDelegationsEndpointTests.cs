using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.SessionSources;
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
            Substitute.For<IEventBroadcaster>(),
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
            BuildSessionRepository(null),
            Substitute.For<IProjectRepository>(),
            BuildSessionOrchestrator());

        var delegationService = new DelegationService(
            Substitute.For<IDelegationRepository>(),
            Substitute.For<IEventBroadcaster>(),
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

    private static ISessionRepository BuildSessionRepository(Session? session)
    {
        var repository = Substitute.For<ISessionRepository>();
        repository.GetByIdAsync(Arg.Any<string>()).Returns(session);
        return repository;
    }

    private static SessionOrchestrator BuildSessionOrchestrator()
    {
        var userContext = new TestUserContext("user-1");
        var options = new WeaveFleet.Application.Configuration.FleetOptions();
        var sessionRepository = Substitute.For<ISessionRepository>();
        var workspaceRepository = Substitute.For<IWorkspaceRepository>();
        var workspaceRootRepository = Substitute.For<IWorkspaceRootRepository>();
        var instanceRepository = Substitute.For<IInstanceRepository>();
        var projectRepository = Substitute.For<IProjectRepository>();
        var callbackRepository = Substitute.For<ISessionCallbackRepository>();
        var delegationRepository = Substitute.For<IDelegationRepository>();
        var sessionSourceUsageRepository = Substitute.For<ISessionSourceUsageRepository>();
        var eventBroadcaster = Substitute.For<IEventBroadcaster>();
        var analyticsCollector = Substitute.For<WeaveFleet.Application.Analytics.IAnalyticsCollector>();
        var messageRepository = Substitute.For<IMessageRepository>();
        var credentialStore = Substitute.For<ICredentialStore>();
        credentialStore.GetDecryptedCredentialsAsync(Arg.Any<string>()).Returns([]);
        workspaceRootRepository.ListAsync().Returns([
            new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") }
        ]);
        var workspaceRootService = new WorkspaceRootService(workspaceRootRepository, userContext);
        var sessionSourceResolutionService = new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService)
        ]);

        return new SessionOrchestrator(
            new WorkspaceService(
                workspaceRepository,
                userContext,
                options,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceService>.Instance),
            new InstanceService(instanceRepository, sessionRepository, userContext),
            sessionSourceResolutionService,
            Substitute.For<WeaveFleet.Application.Harnesses.IHarnessRegistry>(),
            new InstanceTracker(),
            sessionRepository,
            sessionSourceUsageRepository,
            callbackRepository,
            delegationRepository,
            projectRepository,
            eventBroadcaster,
            analyticsCollector,
            messageRepository,
            new DelegationService(delegationRepository, eventBroadcaster, userContext),
            credentialStore,
            userContext,
            options,
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
