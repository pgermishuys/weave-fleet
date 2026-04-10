using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

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
    public async Task UpdateRetention_WhenUnarchiving_ReturnsNoContent()
    {
        var service = BuildSessionService(MakeSession("session-1", "stopped", "archived"));

        var result = await InvokeUpdateRetention("session-1", new UpdateSessionRetentionRequest("active"), service);

        result.ShouldBeOfType<NoContent>();
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
        var sessionRepository = Substitute.For<ISessionRepository>();
        sessionRepository.ListAsync(
            25,
            5,
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>?>())
            .Returns([MakeSession("session-1", "active", "active")]);
        var service = BuildSessionService(sessionRepository);

        var result = await service.ListSessionsAsync(25, 5, null, null, null);

        result.IsSuccess.ShouldBeTrue();
        await sessionRepository.Received(1).ListAsync(
            25,
            5,
            Arg.Is<IReadOnlyList<string>?>(value => value == null),
            Arg.Is<string?>(value => value == null),
            Arg.Is<IReadOnlyList<string>?>(value => value != null && value.Count == 1 && value[0] == "active"));
    }

    [Fact]
    public async Task ListSessions_WhenRetentionFilterAll_OmitsRetentionConstraint()
    {
        var sessionRepository = Substitute.For<ISessionRepository>();
        sessionRepository.ListAsync(
            25,
            5,
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyList<string>?>())
            .Returns([MakeSession("session-1", "active", "active")]);
        var service = BuildSessionService(sessionRepository);

        var result = await service.ListSessionsAsync(25, 5, null, null, "all");

        result.IsSuccess.ShouldBeTrue();
        await sessionRepository.Received(1).ListAsync(
            25,
            5,
            Arg.Is<IReadOnlyList<string>?>(value => value == null),
            Arg.Is<string?>(value => value == null),
            Arg.Is<IReadOnlyList<string>?>(value => value == null));
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
        var sessionRepository = Substitute.For<ISessionRepository>();
        sessionRepository.GetByIdAsync(Arg.Any<string>()).Returns(callInfo =>
        {
            var id = callInfo.Arg<string>();
            return session is not null && session.Id == id ? session : null;
        });
        sessionRepository.DeleteAsync(Arg.Any<string>()).Returns(true);
        return BuildSessionService(sessionRepository);
    }

    private static SessionService BuildSessionService(ISessionRepository sessionRepository)
    {
        sessionRepository.DeleteAsync(Arg.Any<string>()).Returns(true);

        var instanceRepository = Substitute.For<IInstanceRepository>();
        instanceRepository.GetByIdAsync(Arg.Any<string>()).Returns(new Instance
        {
            Id = "inst-1",
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });
        instanceRepository.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(Task.CompletedTask);

        var projectRepository = Substitute.For<IProjectRepository>();
        var callbackRepository = Substitute.For<ISessionCallbackRepository>();
        var delegationRepository = Substitute.For<IDelegationRepository>();
        var sessionSourceUsageRepository = Substitute.For<ISessionSourceUsageRepository>();
        var eventBroadcaster = Substitute.For<IEventBroadcaster>();
        var analyticsCollector = Substitute.For<WeaveFleet.Application.Analytics.IAnalyticsCollector>();
        var messageRepository = Substitute.For<IMessageRepository>();
        var workspaceRepository = Substitute.For<IWorkspaceRepository>();
        var workspaceRootRepository = Substitute.For<IWorkspaceRootRepository>();
        workspaceRootRepository.ListAsync().Returns([
            new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O") }
        ]);
        var workspaceRootService = new WorkspaceRootService(workspaceRootRepository);
        var sessionSourceResolutionService = new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService)
        ]);

        var orchestrator = new SessionOrchestrator(
            new WorkspaceService(
                workspaceRepository,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceService>.Instance),
            new InstanceService(instanceRepository, sessionRepository),
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
            new DelegationService(delegationRepository, eventBroadcaster),
            new WeaveFleet.Application.Configuration.FleetOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionOrchestrator>.Instance);

        return new SessionService(sessionRepository, projectRepository, orchestrator);
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
