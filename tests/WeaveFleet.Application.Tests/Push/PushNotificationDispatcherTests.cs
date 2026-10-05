using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Push;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Push;

public sealed class PushNotificationDispatcherTests : IDisposable
{
    private readonly InMemoryPushSubscriptionRepository _subscriptions = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
    private readonly DeskPresenceTracker _presence;
    private readonly RecordingSender _sender = new();
    private readonly PushNotificationDispatcher _sut;

    public PushNotificationDispatcherTests()
    {
        _presence = new DeskPresenceTracker(_time);
        _sut = new PushNotificationDispatcher(_subscriptions, [_sender], _presence, _time, NullLogger<PushNotificationDispatcher>.Instance);
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task Each_subscription_gets_only_the_kinds_it_chose()
    {
        await Subscribe("https://push.example/all", SessionNotificationKinds.All);
        await Subscribe("https://push.example/asks", [SessionNotificationKinds.Permission, SessionNotificationKinds.Question]);

        await _sut.SendAsync(Notification(SessionNotificationKinds.Finished), CancellationToken.None);
        await _sut.SendAsync(Notification(SessionNotificationKinds.Permission, "perm-1"), CancellationToken.None);

        _sender.Sent.Select(s => (s.Endpoint, Payload(s).GetProperty("kind").GetString()))
            .ShouldBe([("https://push.example/all", "finished"), ("https://push.example/all", "permission"), ("https://push.example/asks", "permission")]);
    }

    [Fact]
    public async Task The_push_carries_where_to_go_and_no_secrets()
    {
        await Subscribe("https://push.example/1", SessionNotificationKinds.All);

        await _sut.SendAsync(Notification(SessionNotificationKinds.Permission, "perm-1") with { MachineId = "falcon-id", MachineName = "falcon" }, CancellationToken.None);

        var payload = Payload(_sender.Sent.Single());
        payload.GetProperty("v").GetInt32().ShouldBe(1);
        payload.GetProperty("machineId").GetString().ShouldBe("falcon-id");
        payload.GetProperty("machineName").GetString().ShouldBe("falcon");
        payload.GetProperty("sessionId").GetString().ShouldBe("s1");
        payload.GetProperty("url").GetString().ShouldBe("/phone/s/falcon-id/s1?ask=perm-1");
        payload.GetProperty("tag").GetString().ShouldBe("falcon-id:s1");
        payload.GetProperty("requestId").GetString().ShouldBe("perm-1");
        _sender.Sent.Single().Message.Urgent.ShouldBeTrue();
        _sender.Sent.Single().Message.Payload.Length.ShouldBeLessThan(1024);
    }

    [Fact]
    public async Task A_finished_turn_is_not_urgent_and_has_no_request()
    {
        await Subscribe("https://push.example/1", SessionNotificationKinds.All);

        await _sut.SendAsync(Notification(SessionNotificationKinds.Finished, "ignored"), CancellationToken.None);

        var sent = _sender.Sent.Single();
        sent.Message.Urgent.ShouldBeFalse();
        Payload(sent).TryGetProperty("requestId", out _).ShouldBeFalse();
        Payload(sent).GetProperty("url").GetString().ShouldBe("/phone/s/local/s1");
    }

    [Fact]
    public async Task Quiet_at_the_desk_holds_pushes_while_a_computer_has_fleet_on_screen()
    {
        await Subscribe("https://push.example/quiet", SessionNotificationKinds.All, quiet: true);
        await Subscribe("https://push.example/loud", SessionNotificationKinds.All, quiet: false);
        _presence.Set("desk-tab", visible: true, DeskPresenceTracker.Desktop);
        _presence.Set("phone-tab", visible: true, DeskPresenceTracker.Phone);

        await _sut.SendAsync(Notification(SessionNotificationKinds.Finished), CancellationToken.None);
        _sender.Sent.Select(s => s.Endpoint).ShouldBe(["https://push.example/loud"]);

        _time.Advance(DeskPresenceTracker.Lifetime + TimeSpan.FromSeconds(1));
        await _sut.SendAsync(Notification(SessionNotificationKinds.Finished), CancellationToken.None);
        _sender.Sent.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_hidden_desktop_window_does_not_count_as_at_the_desk()
    {
        await Subscribe("https://push.example/quiet", SessionNotificationKinds.All, quiet: true);
        _presence.Set("desk-tab", visible: false, DeskPresenceTracker.Desktop);

        await _sut.SendAsync(Notification(SessionNotificationKinds.Finished), CancellationToken.None);

        _sender.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_gone_subscription_is_deleted()
    {
        await Subscribe("https://push.example/1", SessionNotificationKinds.All);
        _sender.Outcome = PushSendOutcome.Gone;

        await _sut.SendAsync(Notification(SessionNotificationKinds.Finished), CancellationToken.None);

        _subscriptions.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ten_failures_in_a_row_delete_the_subscription_and_a_success_resets_the_count()
    {
        await Subscribe("https://push.example/1", SessionNotificationKinds.All);
        _sender.Outcome = PushSendOutcome.RetryLater;
        for (var i = 0; i < 9; i++)
            await _sut.SendAsync(Notification(SessionNotificationKinds.Finished), CancellationToken.None);
        _subscriptions.All.Single().FailureCount.ShouldBe(9);

        _sender.Outcome = PushSendOutcome.Delivered;
        await _sut.SendAsync(Notification(SessionNotificationKinds.Finished), CancellationToken.None);
        _subscriptions.All.Single().FailureCount.ShouldBe(0);

        _sender.Outcome = PushSendOutcome.Failed;
        for (var i = 0; i < PushNotificationDispatcher.MaxFailures; i++)
            await _sut.SendAsync(Notification(SessionNotificationKinds.Finished), CancellationToken.None);
        _subscriptions.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_older_fleets_needs_you_reads_as_a_permission()
    {
        await Subscribe("https://push.example/1", [SessionNotificationKinds.Permission]);

        await _sut.SendAsync(Notification(null) with { Reason = SessionNotificationReasons.NeedsYou }, CancellationToken.None);

        Payload(_sender.Sent.Single()).GetProperty("kind").GetString().ShouldBe("permission");
    }

    [Fact]
    public async Task Queued_notifications_go_out_in_the_background()
    {
        await Subscribe("https://push.example/1", SessionNotificationKinds.All);

        await _sut.HandleAsync(Notification(SessionNotificationKinds.Finished), "u1", CancellationToken.None);

        for (var i = 0; i < 100 && _sender.Sent.Count == 0; i++)
            await Task.Delay(10);
        _sender.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        // The container disposes it once as itself and once as a notification sink.
        _sut.Dispose();
        Should.NotThrow(() => _sut.Dispose());
    }

    private Task<PushSubscriptionRecord> Subscribe(string endpoint, IReadOnlyList<string> kinds, bool quiet = false) =>
        _subscriptions.UpsertAsync(new PushSubscriptionRecord
        {
            Id = endpoint,
            Endpoint = endpoint,
            P256dh = "k",
            Auth = "a",
            Kinds = kinds,
            QuietWhenDesk = quiet,
            CreatedAt = _time.GetUtcNow().AddTicks(endpoint.Length),
        });

    private static SessionNotificationPayload Notification(string? kind, string? requestId = null) => new()
    {
        SessionId = "s1",
        Reason = kind == SessionNotificationKinds.Finished ? SessionNotificationReasons.Finished : SessionNotificationReasons.NeedsYou,
        Kind = kind,
        RequestId = requestId,
        Title = "Fix flaky SignalR reconnect test",
        Body = "Wants to run dotnet test",
    };

    private static JsonElement Payload((string Endpoint, PushMessage Message) sent) => JsonDocument.Parse(sent.Message.Payload).RootElement;

    private sealed class RecordingSender : IPushSender
    {
        public PushSendOutcome Outcome { get; set; } = PushSendOutcome.Delivered;

        public List<(string Endpoint, PushMessage Message)> Sent { get; } = [];

        public string Channel => "webpush";

        public Task<PushSendResult> SendAsync(PushSubscriptionRecord subscription, PushMessage message, CancellationToken cancellationToken)
        {
            lock (Sent)
                Sent.Add((subscription.Endpoint, message));
            return Task.FromResult(new PushSendResult(Outcome));
        }
    }
}
