using System.Collections.Concurrent;
using Shouldly;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Unit tests for <see cref="SessionActivityTracker"/>.
/// </summary>
public sealed class SessionActivityTrackerTests
{
    [Fact]
    public void UpdateSetsActivityStatus()
    {
        var sut = new SessionActivityTracker();

        sut.Update("session-1", "busy", "user-1");

        var snapshot = sut.Get("session-1");
        snapshot.ShouldNotBeNull();
        snapshot.FleetSessionId.ShouldBe("session-1");
        snapshot.ActivityStatus.ShouldBe("busy");
        snapshot.UserId.ShouldBe("user-1");
        snapshot.UpdatedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(-5));
    }

    [Fact]
    public void RemoveClearsState()
    {
        var sut = new SessionActivityTracker();
        sut.Update("session-1", "busy", "user-1");

        sut.Remove("session-1");

        sut.Get("session-1").ShouldBeNull();
    }

    [Fact]
    public void RemoveOnUnknownSessionDoesNotThrow()
    {
        var sut = new SessionActivityTracker();

        Should.NotThrow(() => sut.Remove("nonexistent-session"));
    }

    [Fact]
    public void GetAllReturnsAllTrackedSessions()
    {
        var sut = new SessionActivityTracker();
        sut.Update("session-1", "busy", "user-1");
        sut.Update("session-2", "idle", "user-2");
        sut.Update("session-3", "busy", "user-1");

        var all = sut.GetAll();

        all.Count.ShouldBe(3);
        all.ContainsKey("session-1").ShouldBeTrue();
        all.ContainsKey("session-2").ShouldBeTrue();
        all.ContainsKey("session-3").ShouldBeTrue();
    }

    [Fact]
    public void UpdateOverwritesPreviousState()
    {
        var sut = new SessionActivityTracker();
        sut.Update("session-1", "busy", "user-1");
        sut.Update("session-1", "idle", "user-1");
        sut.Update("session-1", "busy", "user-1");

        var snapshot = sut.Get("session-1");
        snapshot.ShouldNotBeNull();
        snapshot.ActivityStatus.ShouldBe("busy");
    }

    [Fact]
    public void GetReturnsNullForUnknownSession()
    {
        var sut = new SessionActivityTracker();

        sut.Get("nonexistent").ShouldBeNull();
    }

    [Fact]
    public void GetAllReturnsEmptyWhenNoSessionsTracked()
    {
        var sut = new SessionActivityTracker();

        sut.GetAll().Count.ShouldBe(0);
    }

    [Fact]
    public void UpdateAcceptsNullUserId()
    {
        var sut = new SessionActivityTracker();

        sut.Update("session-1", "busy", null);

        var snapshot = sut.Get("session-1");
        snapshot.ShouldNotBeNull();
        snapshot.UserId.ShouldBeNull();
    }

    // ── Parent-child relationship tests ──────────────────────────────────────

    [Fact]
    public void RegisterChild_WhenChildBecomesbusy_GetEffectiveActivityStatus_ReturnsParentBusy()
    {
        var sut = new SessionActivityTracker();
        sut.Update("child-1", "busy", "user-1");

        sut.RegisterChild("child-1", "parent-1");

        sut.GetEffectiveActivityStatus("parent-1").ShouldBe("busy");
    }

    [Fact]
    public void GetEffectiveActivityStatus_WhenChildIdleAndParentIdle_ReturnsIdle()
    {
        var sut = new SessionActivityTracker();
        sut.Update("parent-1", "idle", "user-1");
        sut.Update("child-1", "idle", "user-1");
        sut.RegisterChild("child-1", "parent-1");

        sut.GetEffectiveActivityStatus("parent-1").ShouldBe("idle");
    }

    [Fact]
    public void GetEffectiveActivityStatus_WhenParentBusyAndChildIdle_ReturnsBusy()
    {
        var sut = new SessionActivityTracker();
        sut.Update("parent-1", "busy", "user-1");
        sut.Update("child-1", "idle", "user-1");
        sut.RegisterChild("child-1", "parent-1");

        sut.GetEffectiveActivityStatus("parent-1").ShouldBe("busy");
    }

    [Fact]
    public void GetEffectiveActivityStatus_WhenMultipleChildren_RemainsbuysyUntilLastChildGoesIdle()
    {
        var sut = new SessionActivityTracker();
        sut.Update("parent-1", "idle", "user-1");
        sut.Update("child-1", "busy", "user-1");
        sut.Update("child-2", "busy", "user-1");
        sut.RegisterChild("child-1", "parent-1");
        sut.RegisterChild("child-2", "parent-1");

        sut.GetEffectiveActivityStatus("parent-1").ShouldBe("busy");

        // One child goes idle — still busy because of child-2
        sut.Update("child-1", "idle", "user-1");
        sut.GetEffectiveActivityStatus("parent-1").ShouldBe("busy");

        // Last child goes idle — parent reverts to its own idle status
        sut.Update("child-2", "idle", "user-1");
        sut.GetEffectiveActivityStatus("parent-1").ShouldBe("idle");
    }

    [Fact]
    public void UnregisterChild_WhenChildUnregistered_ParentReverts()
    {
        var sut = new SessionActivityTracker();
        sut.Update("parent-1", "idle", "user-1");
        sut.Update("child-1", "busy", "user-1");
        sut.RegisterChild("child-1", "parent-1");

        sut.GetEffectiveActivityStatus("parent-1").ShouldBe("busy");

        sut.UnregisterChild("child-1");

        sut.GetEffectiveActivityStatus("parent-1").ShouldBe("idle");
    }

    [Fact]
    public void GetParentSessionId_ReturnsParentForRegisteredChild()
    {
        var sut = new SessionActivityTracker();
        sut.RegisterChild("child-1", "parent-1");

        sut.GetParentSessionId("child-1").ShouldBe("parent-1");
    }

    [Fact]
    public void GetParentSessionId_ReturnsNullForUnregisteredSession()
    {
        var sut = new SessionActivityTracker();

        sut.GetParentSessionId("unknown").ShouldBeNull();
    }

    [Fact]
    public void GetEffectiveActivityStatus_WhenSessionNotTracked_ReturnsNull()
    {
        var sut = new SessionActivityTracker();

        sut.GetEffectiveActivityStatus("nonexistent").ShouldBeNull();
    }

    [Fact]
    public void Remove_WhenChildRemoved_ParentReverts()
    {
        var sut = new SessionActivityTracker();
        sut.Update("parent-1", "idle", "user-1");
        sut.Update("child-1", "busy", "user-1");
        sut.RegisterChild("child-1", "parent-1");

        sut.Remove("child-1");

        sut.GetEffectiveActivityStatus("parent-1").ShouldBe("idle");
        sut.GetParentSessionId("child-1").ShouldBeNull();
    }

    [Fact]
    public void Remove_WhenParentRemoved_ChildToParentMappingCleaned()
    {
        var sut = new SessionActivityTracker();
        sut.Update("parent-1", "busy", "user-1");
        sut.Update("child-1", "idle", "user-1");
        sut.RegisterChild("child-1", "parent-1");

        sut.Remove("parent-1");

        sut.GetParentSessionId("child-1").ShouldBeNull();
    }

    [Fact]
    public void UnregisterChild_OnUnknownChild_DoesNotThrow()
    {
        var sut = new SessionActivityTracker();

        Should.NotThrow(() => sut.UnregisterChild("nonexistent"));
    }

    [Fact]
    public async Task ConcurrentUpdatesDoNotThrow()
    {
        var sut = new SessionActivityTracker();
        var exceptions = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            try
            {
                sut.Update($"session-{i % 10}", i % 2 == 0 ? "busy" : "idle", $"user-{i % 5}");
                _ = sut.Get($"session-{i % 10}");
                _ = sut.GetAll();
                if (i % 7 == 0)
                    sut.Remove($"session-{i % 10}");
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        exceptions.ShouldBeEmpty();
    }
}
