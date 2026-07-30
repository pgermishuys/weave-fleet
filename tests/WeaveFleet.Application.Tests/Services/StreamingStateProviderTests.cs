using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Unit tests for <see cref="StreamingStateProvider"/>.
/// </summary>
public sealed class StreamingStateProviderTests
{
    [Fact]
    public void GetStreamingState_WhenNoState_ReturnsEmptySnapshot()
    {
        var activityTracker = new SessionActivityTracker();
        var deltaBuffer = new TextDeltaBuffer();
        var sut = new StreamingStateProvider(activityTracker, deltaBuffer);

        var snapshot = sut.GetStreamingState("session-1");

        snapshot.ActivitySnapshot.ShouldBeNull();
        snapshot.BufferedDeltas.ShouldBeEmpty();
    }

    [Fact]
    public void GetStreamingState_WhenActivityOnly_ReturnsActivityWithoutDeltas()
    {
        var activityTracker = new SessionActivityTracker();
        var deltaBuffer = new TextDeltaBuffer();
        var sut = new StreamingStateProvider(activityTracker, deltaBuffer);

        activityTracker.Update("session-1", "busy", "user-1");

        var snapshot = sut.GetStreamingState("session-1");

        snapshot.ActivitySnapshot.ShouldNotBeNull();
        snapshot.ActivitySnapshot.FleetSessionId.ShouldBe("session-1");
        snapshot.ActivitySnapshot.ActivityStatus.ShouldBe("busy");
        snapshot.ActivitySnapshot.UserId.ShouldBe("user-1");
        snapshot.BufferedDeltas.ShouldBeEmpty();
    }

    [Fact]
    public void GetStreamingState_WhenDeltasOnly_ReturnsDeltasWithoutActivity()
    {
        var activityTracker = new SessionActivityTracker();
        var deltaBuffer = new TextDeltaBuffer();
        var sut = new StreamingStateProvider(activityTracker, deltaBuffer);

        deltaBuffer.Append("session-1", "msg-1", "p1", "hello");
        deltaBuffer.Append("session-1", "msg-1", "p2", "world");

        var snapshot = sut.GetStreamingState("session-1");

        snapshot.ActivitySnapshot.ShouldBeNull();
        snapshot.BufferedDeltas.Count.ShouldBe(1);
        snapshot.BufferedDeltas.ContainsKey("msg-1").ShouldBeTrue();
        snapshot.BufferedDeltas["msg-1"]["p1"].ShouldBe("hello");
        snapshot.BufferedDeltas["msg-1"]["p2"].ShouldBe("world");
    }

    [Fact]
    public void GetStreamingState_WhenBothPresent_ReturnsBoth()
    {
        var activityTracker = new SessionActivityTracker();
        var deltaBuffer = new TextDeltaBuffer();
        var sut = new StreamingStateProvider(activityTracker, deltaBuffer);

        activityTracker.Update("session-1", "idle", "user-1");
        deltaBuffer.Append("session-1", "msg-1", "p1", "test");

        var snapshot = sut.GetStreamingState("session-1");

        snapshot.ActivitySnapshot.ShouldNotBeNull();
        snapshot.ActivitySnapshot.ActivityStatus.ShouldBe("idle");
        snapshot.BufferedDeltas.Count.ShouldBe(1);
        snapshot.BufferedDeltas["msg-1"]["p1"].ShouldBe("test");
    }

    [Fact]
    public void GetStreamingState_GroupsDeltasByMessage()
    {
        var activityTracker = new SessionActivityTracker();
        var deltaBuffer = new TextDeltaBuffer();
        var sut = new StreamingStateProvider(activityTracker, deltaBuffer);

        deltaBuffer.Append("session-1", "msg-1", "p1", "a");
        deltaBuffer.Append("session-1", "msg-1", "p2", "b");
        deltaBuffer.Append("session-1", "msg-2", "p1", "c");
        deltaBuffer.Append("session-1", "msg-2", "p2", "d");

        var snapshot = sut.GetStreamingState("session-1");

        snapshot.BufferedDeltas.Count.ShouldBe(2);
        snapshot.BufferedDeltas["msg-1"].Count.ShouldBe(2);
        snapshot.BufferedDeltas["msg-1"]["p1"].ShouldBe("a");
        snapshot.BufferedDeltas["msg-1"]["p2"].ShouldBe("b");
        snapshot.BufferedDeltas["msg-2"].Count.ShouldBe(2);
        snapshot.BufferedDeltas["msg-2"]["p1"].ShouldBe("c");
        snapshot.BufferedDeltas["msg-2"]["p2"].ShouldBe("d");
    }

    [Fact]
    public void GetStreamingState_IsolatesSessionState()
    {
        var activityTracker = new SessionActivityTracker();
        var deltaBuffer = new TextDeltaBuffer();
        var sut = new StreamingStateProvider(activityTracker, deltaBuffer);

        activityTracker.Update("session-1", "busy", "user-1");
        activityTracker.Update("session-2", "idle", "user-2");
        deltaBuffer.Append("session-1", "msg-1", "p1", "a");
        deltaBuffer.Append("session-2", "msg-2", "p1", "b");

        var snapshot1 = sut.GetStreamingState("session-1");
        var snapshot2 = sut.GetStreamingState("session-2");

        snapshot1.ActivitySnapshot!.ActivityStatus.ShouldBe("busy");
        snapshot1.BufferedDeltas.ShouldHaveSingleItem();
        snapshot1.BufferedDeltas.ContainsKey("msg-1").ShouldBeTrue();

        snapshot2.ActivitySnapshot!.ActivityStatus.ShouldBe("idle");
        snapshot2.BufferedDeltas.ShouldHaveSingleItem();
        snapshot2.BufferedDeltas.ContainsKey("msg-2").ShouldBeTrue();
    }

    [Fact]
    public void GetStreamingState_HandlesMultiplePartsInSingleMessage()
    {
        var activityTracker = new SessionActivityTracker();
        var deltaBuffer = new TextDeltaBuffer();
        var sut = new StreamingStateProvider(activityTracker, deltaBuffer);

        deltaBuffer.Append("session-1", "msg-1", "p1", "first");
        deltaBuffer.Append("session-1", "msg-1", "p2", "second");
        deltaBuffer.Append("session-1", "msg-1", "p3", "third");

        var snapshot = sut.GetStreamingState("session-1");

        snapshot.BufferedDeltas.ShouldHaveSingleItem();
        snapshot.BufferedDeltas["msg-1"].Count.ShouldBe(3);
        snapshot.BufferedDeltas["msg-1"]["p1"].ShouldBe("first");
        snapshot.BufferedDeltas["msg-1"]["p2"].ShouldBe("second");
        snapshot.BufferedDeltas["msg-1"]["p3"].ShouldBe("third");
    }

    [Fact]
    public void GetStreamingState_ReturnsReadOnlyDictionaries()
    {
        var activityTracker = new SessionActivityTracker();
        var deltaBuffer = new TextDeltaBuffer();
        var sut = new StreamingStateProvider(activityTracker, deltaBuffer);

        deltaBuffer.Append("session-1", "msg-1", "p1", "test");

        var snapshot = sut.GetStreamingState("session-1");

        snapshot.BufferedDeltas.ShouldBeAssignableTo<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>();
        snapshot.BufferedDeltas["msg-1"].ShouldBeAssignableTo<IReadOnlyDictionary<string, string>>();
    }

    [Fact]
    public void GetStreamingState_WhenActivityWithNullUserId_ReturnsSnapshot()
    {
        var activityTracker = new SessionActivityTracker();
        var deltaBuffer = new TextDeltaBuffer();
        var sut = new StreamingStateProvider(activityTracker, deltaBuffer);

        activityTracker.Update("session-1", "busy", null);

        var snapshot = sut.GetStreamingState("session-1");

        snapshot.ActivitySnapshot.ShouldNotBeNull();
        snapshot.ActivitySnapshot.UserId.ShouldBeNull();
        snapshot.ActivitySnapshot.ActivityStatus.ShouldBe("busy");
    }

    [Fact]
    public void GetStreamingState_AccumulatedDeltas_AreIncluded()
    {
        var activityTracker = new SessionActivityTracker();
        var deltaBuffer = new TextDeltaBuffer();
        var sut = new StreamingStateProvider(activityTracker, deltaBuffer);

        deltaBuffer.Append("session-1", "msg-1", "p1", "hel");
        deltaBuffer.Append("session-1", "msg-1", "p1", "lo");

        var snapshot = sut.GetStreamingState("session-1");

        snapshot.BufferedDeltas["msg-1"]["p1"].ShouldBe("hello");
    }
}
