using Shouldly;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Desktop;

public sealed class DesktopEndpointsTests
{
    private static SessionActivitySnapshot Session(string id, string status, string? userId = "local-user") =>
        new(id, status, userId, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Counts_sessions_that_are_working_or_waiting_to_retry()
    {
        var sessions = new[] { Session("a", "busy"), Session("b", "idle"), Session("c", "busy"), Session("d", "retry") };

        DesktopEndpoints.CountWorking(sessions, "local-user").ShouldBe(3);
    }

    [Fact]
    public void Counts_only_your_own_sessions()
    {
        var sessions = new[] { Session("a", "busy", "someone-else"), Session("b", "busy"), Session("c", "busy", userId: null) };

        DesktopEndpoints.CountWorking(sessions, "local-user").ShouldBe(2);
    }
}
