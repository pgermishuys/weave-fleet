using System.Text.Json;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class SmartLinkDetectorTests
{
    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Text_parts_are_mentions()
    {
        var links = SmartLinkDetector.FindLinks(Payload("""
            { "part": { "type": "text", "text": "PR is up: https://github.com/owner/repo/pull/12" } }
            """));

        var (reference, relationship) = links.ShouldHaveSingleItem();
        reference.Url.ShouldBe("https://github.com/owner/repo/pull/12");
        relationship.ShouldBe(SmartLinkRelationships.Mentioned);
    }

    [Fact]
    public void Streaming_text_waits_for_a_trailing_link_to_finish()
    {
        var streaming = SmartLinkDetector.FindLinks(Payload("""
            { "part": { "type": "text", "text": "PR: https://github.com/owner/repo/pull/1", "time": { "start": 1 } } }
            """));
        var complete = SmartLinkDetector.FindLinks(Payload("""
            { "part": { "type": "text", "text": "PR: https://github.com/owner/repo/pull/12", "time": { "start": 1, "end": 2 } } }
            """));

        streaming.ShouldBeEmpty();
        complete.ShouldHaveSingleItem().Reference.Number.ShouldBe(12);
    }

    [Fact]
    public void Pull_request_urls_in_gh_pr_create_output_are_the_sessions_own()
    {
        var links = SmartLinkDetector.FindLinks(Payload("""
            {
              "part": {
                "type": "tool",
                "tool": "bash",
                "state": {
                  "status": "completed",
                  "input": { "command": "gh pr create --fill --base main" },
                  "output": "Creating pull request for feat/x into main\n\nhttps://github.com/owner/repo/pull/13\n"
                }
              }
            }
            """));

        var (reference, relationship) = links.ShouldHaveSingleItem();
        reference.Number.ShouldBe(13);
        relationship.ShouldBe(SmartLinkRelationships.Own);
    }

    [Fact]
    public void Other_tool_output_is_ignored()
    {
        // Listing pull requests shouldn't attach every one of them to the session.
        var links = SmartLinkDetector.FindLinks(Payload("""
            {
              "part": {
                "type": "tool",
                "state": {
                  "input": { "command": "gh pr list" },
                  "output": "https://github.com/owner/repo/pull/1\nhttps://github.com/owner/repo/pull/2"
                }
              }
            }
            """));

        links.ShouldBeEmpty();
    }

    [Fact]
    public void Observe_queues_each_link_once_per_session_and_relationship()
    {
        var detector = new SmartLinkDetector();
        var payload = Payload("""{ "part": { "type": "text", "text": "https://github.com/owner/repo/issues/3" } }""");

        detector.Observe("s1", "u1", EventTypes.MessagePartUpdated, payload);
        detector.Observe("s1", "u1", EventTypes.MessagePartUpdated, payload);
        detector.Observe("s2", "u1", EventTypes.MessagePartUpdated, payload);
        detector.Observe("s3", "u1", EventTypes.MessageUpdated, payload);
        detector.Observe("s4", null, EventTypes.MessagePartUpdated, payload);

        var queued = new List<DetectedSmartLink>();
        while (detector.Reader.TryRead(out var item))
            queued.Add(item);

        queued.Select(q => q.SessionId).ShouldBe(["s1", "s2"]);
    }

    [Fact]
    public void ActiveSince_returns_sessions_that_sent_any_event_since_the_cutoff()
    {
        var clock = new StubClock(DateTimeOffset.Parse("2026-09-14T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var detector = new SmartLinkDetector(clock);

        detector.Observe("early", "u1", EventTypes.MessageUpdated, null);
        clock.Now = clock.Now.AddMinutes(10);
        detector.Observe("late", null, "session.status", null);

        detector.ActiveSince(clock.Now.AddMinutes(-5)).ShouldBe(["late"]);
        // Sessions older than a cutoff are forgotten.
        detector.ActiveSince(clock.Now.AddMinutes(-20)).ShouldBe(["late"]);
    }

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
