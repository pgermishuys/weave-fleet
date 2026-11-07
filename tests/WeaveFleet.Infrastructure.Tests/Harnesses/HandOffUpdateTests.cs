using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses.Acp;
using WeaveFleet.Infrastructure.Harnesses.Pi;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

/// <summary>
/// A session that messages a Pi or GitHub Copilot session with <c>notifyWhenDone</c> hears back when the turn that took
/// the message ends: the harness's events, as the relay translates them, arm the watch on the message they answer.
/// </summary>
public sealed class HandOffUpdateTests
{
    private const string Asker = "s-asker";
    private const string Target = "s-target";
    private const string Delivered = "msg_from_asker";

    private readonly FakeSender _sender = new();
    private readonly SessionUpdates _updates;
    private readonly DomainEventTranslator _translator = new(NullLogger<DomainEventTranslator>.Instance);

    public HandOffUpdateTests()
    {
        _updates = new SessionUpdates(
            new SessionActivityTracker(),
            TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
                services.AddSingleton<ISessionUpdateSender>(_sender);
            }),
            NullLogger<SessionUpdates>.Instance);
        _updates.Watch(new SessionUpdateWatch(Asker, Target, "u1", Delivered));
    }

    [Fact]
    public async Task a_pi_session_that_took_the_message_reports_back_once_its_run_ends()
    {
        var mapper = new PiMapper(Target);
        var calling = new PiMessage
        {
            Role = "assistant",
            Content = [new PiToolCallContent { Id = "call-1", Name = "bash", Arguments = JsonDocument.Parse("{}").RootElement.Clone() }],
            StopReason = "toolUse",
            Timestamp = 1000,
        };
        var answer = new PiMessage { Role = "assistant", Content = [new PiTextContent { Text = "2 failed." }], StopReason = "stop", Timestamp = 2000 };

        mapper.Prompting(Delivered);
        await RelayAsync(mapper.Map(new PiAgentStartEvent()));
        await RelayAsync(mapper.Map(new PiMessageStartEvent { Message = calling }));
        await RelayAsync(mapper.Map(new PiMessageEndEvent { Message = calling }));
        await RelayAsync(mapper.Map(new PiTurnEndEvent { Message = calling }));

        // Pi is running the call: the run isn't over, so nothing is reported half way.
        _sender.Sent.ShouldBeEmpty();

        await RelayAsync(mapper.Map(new PiTurnStartEvent()));
        await RelayAsync(mapper.Map(new PiMessageStartEvent { Message = answer }));
        await RelayAsync(mapper.Map(new PiMessageEndEvent { Message = answer }));
        await RelayAsync(mapper.Map(new PiTurnEndEvent { Message = answer }));
        await RelayAsync(mapper.Map(new PiAgentEndEvent { Messages = [calling, answer] }));

        var sent = _sender.Sent.ShouldHaveSingleItem();
        sent.AskerId.ShouldBe(Asker);
        sent.TargetId.ShouldBe(Target);
        sent.Outcome.ShouldBe(SessionUpdates.Finished);
    }

    [Fact]
    public async Task a_copilot_session_that_took_the_message_reports_back_once_its_turn_ends()
    {
        var mapper = new AcpMapper(Target);

        await RelayAsync(mapper.UserPrompt("<fleet-session-message …>", messageId: Delivered));
        await RelayAsync(mapper.Status(ActivityStatuses.Busy));
        await RelayAsync(mapper.Update(JsonSerializer.SerializeToElement(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "2 failed." } })));
        await RelayAsync(mapper.EndTurn("end_turn"));

        var sent = _sender.Sent.ShouldHaveSingleItem();
        sent.AskerId.ShouldBe(Asker);
        sent.Outcome.ShouldBe(SessionUpdates.Finished);
    }

    private async Task RelayAsync(IEnumerable<HarnessEvent> events)
    {
        foreach (var evt in events)
            _updates.Observe(Target, _translator.Translate(evt with { FleetSessionId = Target }));
        await _updates.Pending;
    }

    private async Task RelayAsync(HarnessEvent evt) => await RelayAsync([evt]);

    private sealed class FakeSender : ISessionUpdateSender
    {
        public List<SessionUpdate> Sent { get; } = [];

        public Task<SessionUpdate?> ReadAsync(SessionUpdateWatch watch, TurnError? failure, CancellationToken ct)
            => Task.FromResult<SessionUpdate?>(new SessionUpdate(watch.AskerId, watch.TargetId, watch.UserId, "…", SessionUpdates.OutcomeOf(failure)));

        public Task<bool> SendAsync(SessionUpdate update, CancellationToken ct)
        {
            Sent.Add(update);
            return Task.FromResult(true);
        }
    }

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
