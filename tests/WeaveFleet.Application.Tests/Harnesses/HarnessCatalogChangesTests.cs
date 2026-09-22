using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Harnesses;

public sealed class HarnessCatalogChangesTests
{
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly HarnessCatalogChanges _changes;

    public HarnessCatalogChangesTests()
    {
        _changes = new HarnessCatalogChanges(_broadcaster, NullLogger<HarnessCatalogChanges>.Instance);
    }

    [Fact]
    public async Task A_change_is_told_to_its_owner_on_the_sessions_topic()
    {
        await _changes.PublishAsync(new HarnessCatalogChange("opencode2", "owner-1", "/work/rocket/", ["none", "p1"], ["s1"]), CancellationToken.None);

        var sent = _broadcaster.Broadcasts.ShouldHaveSingleItem();
        sent.Topic.ShouldBe("sessions");
        sent.Type.ShouldBe(HarnessCatalogChanges.EventType);
        sent.UserId.ShouldBe("owner-1");
        sent.Payload.GetRawText().ShouldBe(
            """{"harnessType":"opencode2","directory":"/work/rocket","quickChat":false,"profileIds":["none","p1"],"sessionIds":["s1"]}""");
    }

    [Fact]
    public async Task The_quick_chat_folder_is_named_as_such()
    {
        // The composer asks for a quick chat's catalog without naming a folder.
        await _changes.PublishAsync(new HarnessCatalogChange("opencode2", "owner-1", QuickChatSessionSourceProvider.BasePath, ["none"], []), CancellationToken.None);

        _broadcaster.Broadcasts.ShouldHaveSingleItem().Payload.GetProperty("quickChat").GetBoolean().ShouldBeTrue();
    }
}
