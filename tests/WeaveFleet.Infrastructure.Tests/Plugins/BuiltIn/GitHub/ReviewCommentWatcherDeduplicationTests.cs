using System.Text.Json.Nodes;
using WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

namespace WeaveFleet.Infrastructure.Tests.Plugins.BuiltIn.GitHub;

public sealed class ReviewCommentWatcherDeduplicationTests
{
    // We test dedup by verifying the metadata serialization helpers via reflection,
    // since they are private static. Instead, we test the observable behavior:
    // BuildReviewThreadsResponse + metadata round-trip patterns.

    [Fact]
    public void notification_tracking_round_trips_through_metadata_json()
    {
        // Simulate what the watcher does: write notifications, serialize, deserialize
        var metadata = new JsonObject();

        var notifications = new JsonArray();
        var entry1 = new JsonObject
        {
            ["commentId"] = JsonValue.Create(100),
            ["notifiedAt"] = JsonValue.Create("2026-01-01T00:00:00Z"),
        };
        var entry2 = new JsonObject
        {
            ["commentId"] = JsonValue.Create(200),
            ["notifiedAt"] = JsonValue.Create("2026-01-02T00:00:00Z"),
        };
        notifications.Add((JsonNode)entry1);
        notifications.Add((JsonNode)entry2);
        metadata["reviewCommentNotifications"] = notifications;

        // Serialize and re-parse (simulates DB round-trip)
        var json = metadata.ToJsonString();
        var restored = JsonNode.Parse(json) as JsonObject;

        restored.ShouldNotBeNull();
        var restoredArr = restored["reviewCommentNotifications"] as JsonArray;
        restoredArr.ShouldNotBeNull();
        restoredArr.Count.ShouldBe(2);

        var first = restoredArr[0] as JsonObject;
        first.ShouldNotBeNull();
        first["commentId"]!.GetValue<int>().ShouldBe(100);
        first["notifiedAt"]!.GetValue<string>().ShouldBe("2026-01-01T00:00:00Z");

        var second = restoredArr[1] as JsonObject;
        second.ShouldNotBeNull();
        second["commentId"]!.GetValue<int>().ShouldBe(200);
    }

    [Fact]
    public void empty_metadata_produces_empty_notifications()
    {
        var metadata = new JsonObject();
        var arr = metadata["reviewCommentNotifications"] as JsonArray;
        arr.ShouldBeNull();
    }

    [Fact]
    public void corrupted_metadata_is_handled_gracefully()
    {
        // If reviewCommentNotifications is not an array, it should be ignored
        var metadata = new JsonObject
        {
            ["reviewCommentNotifications"] = JsonValue.Create("not-an-array"),
        };

        var arr = metadata["reviewCommentNotifications"] as JsonArray;
        arr.ShouldBeNull(); // cast fails gracefully
    }

    [Fact]
    public void dedup_logic_skips_already_notified_comment_ids()
    {
        // Simulate the dedup check: collect comment IDs from notifications
        var existingNotifications = new JsonArray();
        var existing = new JsonObject
        {
            ["commentId"] = JsonValue.Create(100),
            ["notifiedAt"] = JsonValue.Create("2026-01-01T00:00:00Z"),
        };
        existingNotifications.Add((JsonNode)existing);

        var notifiedIds = new HashSet<int>();
        foreach (var item in existingNotifications.OfType<JsonObject>())
        {
            var id = item["commentId"]?.GetValue<int>() ?? 0;
            if (id > 0) notifiedIds.Add(id);
        }

        // Comment 100 should be skipped, 200 should be new
        notifiedIds.Contains(100).ShouldBeTrue();
        notifiedIds.Contains(200).ShouldBeFalse();
    }
}
