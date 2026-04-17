using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Tests.Services;

public sealed class TextDeltaBufferTests
{
    [Fact]
    public void Append_concatenates_multipleDeltasForSamePart()
    {
        var sut = new TextDeltaBuffer();
        sut.Append("sess-1", "msg-1", "p1", "hel");
        sut.Append("sess-1", "msg-1", "p1", "lo");

        var snap = sut.SnapshotSession("sess-1");
        snap[("msg-1", "p1")].ShouldBe("hello");
    }

    [Fact]
    public void SnapshotSession_scopesToOneSession()
    {
        var sut = new TextDeltaBuffer();
        sut.Append("sess-1", "msg-1", "p1", "a");
        sut.Append("sess-2", "msg-2", "p1", "b");

        sut.SnapshotSession("sess-1").ShouldHaveSingleItem();
        sut.SnapshotSession("sess-2").ShouldHaveSingleItem();
    }

    [Fact]
    public void ClearMessage_removesAllPartsOfThatMessage_butNotOthers()
    {
        var sut = new TextDeltaBuffer();
        sut.Append("sess-1", "msg-1", "p1", "a");
        sut.Append("sess-1", "msg-1", "p2", "b");
        sut.Append("sess-1", "msg-2", "p1", "c");

        sut.ClearMessage("sess-1", "msg-1");

        var snap = sut.SnapshotSession("sess-1");
        snap.ShouldHaveSingleItem();
        snap[("msg-2", "p1")].ShouldBe("c");
    }

    [Fact]
    public void ClearPart_removesOnlyOnePart()
    {
        var sut = new TextDeltaBuffer();
        sut.Append("sess-1", "msg-1", "p1", "a");
        sut.Append("sess-1", "msg-1", "p2", "b");

        sut.ClearPart("sess-1", "msg-1", "p1");

        var snap = sut.SnapshotSession("sess-1");
        snap.ShouldHaveSingleItem();
        snap[("msg-1", "p2")].ShouldBe("b");
    }

    [Fact]
    public void Append_emptyDelta_isNoOp()
    {
        var sut = new TextDeltaBuffer();
        sut.Append("s", "m", "p", "");
        sut.SnapshotSession("s").ShouldBeEmpty();
    }
}
