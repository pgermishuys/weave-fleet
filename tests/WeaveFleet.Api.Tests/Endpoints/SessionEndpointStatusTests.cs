using System.Reflection;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class SessionEndpointStatusTests
{
    [Fact]
    public void DeriveAggregatedSessionStatus_WhenParentHasNoChildren_ReturnsOwnStatus()
    {
        var session = CreateSession("parent-1", "running", "busy");

        var status = InvokeDeriveAggregatedSessionStatus(session, []);

        status.ShouldBe("active");
    }

    [Fact]
    public void DeriveAggregatedSessionStatus_WhenParentHasBusyChild_ReturnsActive()
    {
        var session = CreateSession("parent-1", "running", "idle");

        var status = InvokeDeriveAggregatedSessionStatus(session, ["parent-1"]);

        status.ShouldBe("active");
    }

    [Fact]
    public void DeriveAggregatedSessionStatus_WhenParentChildrenAreAllIdle_ReturnsOwnStatus()
    {
        var session = CreateSession("parent-1", "running", "idle");

        var status = InvokeDeriveAggregatedSessionStatus(session, []);

        status.ShouldBe("idle");
    }

    [Fact]
    public void DeriveAggregatedSessionStatus_WhenSessionIsNotParent_ReturnsUnchangedOwnStatus()
    {
        var session = CreateSession("child-1", "running", "busy", "parent-1");

        var status = InvokeDeriveAggregatedSessionStatus(session, ["other-parent"]);

        status.ShouldBe("active");
    }

    [Fact]
    public void DeriveAggregatedSessionStatus_WhenParentIsTerminal_ReturnsTerminalStatus()
    {
        var session = CreateSession("parent-1", "completed", "idle");

        var status = InvokeDeriveAggregatedSessionStatus(session, ["parent-1"]);

        status.ShouldBe("completed");
    }

    private static string InvokeDeriveAggregatedSessionStatus(Session session, IEnumerable<string> parentIdsWithBusyChildren)
    {
        var method = typeof(SessionEndpoints).GetMethod(
            "DeriveAggregatedSessionStatus",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.ShouldNotBeNull();

        var result = method.Invoke(
            null,
            [session, new HashSet<string>(parentIdsWithBusyChildren, StringComparer.Ordinal)]);

        return result.ShouldBeOfType<string>();
    }

    private static Session CreateSession(string id, string status, string? activityStatus)
        => CreateSession(id, status, activityStatus, null);

    private static Session CreateSession(string id, string status, string? activityStatus, string? parentSessionId)
        => new()
        {
            Id = id,
            WorkspaceId = "ws-1",
            InstanceId = "inst-1",
            OpencodeSessionId = $"oc-{id}",
            Title = "Session",
            Status = status,
            ActivityStatus = activityStatus,
            ParentSessionId = parentSessionId,
            Directory = "/tmp",
            CreatedAt = DateTime.UtcNow.ToString("O")
        };
}
