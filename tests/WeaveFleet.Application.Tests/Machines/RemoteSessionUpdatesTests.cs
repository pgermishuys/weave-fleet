using System.Net;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>
/// The update for a session on another machine: Fleet reads that session's reply there, with the token it keeps, and
/// names the machine in the update so the asker (and the user) can tell where it came from.
/// </summary>
public sealed class RemoteSessionUpdatesTests : IDisposable
{
    private static readonly SessionMessageMachine Atlas = new(FakeMachine.Id, FakeMachine.Name);
    private static readonly SessionUpdateWatch Watch = new("s-asker", "s-remote", "user-1", "msg-1", Atlas, "Run the integration tests");

    private readonly FakeMachine _atlas = new();
    private readonly SessionUpdateSender _sender;
    private int _switchReads;

    private sealed class CountingPreferences(InMemoryUserPreferenceRepository inner, Action read) : WeaveFleet.Domain.Repositories.IUserPreferenceRepository
    {
        public Task<string?> GetAsync(string key)
        {
            read();
            return inner.GetAsync(key);
        }

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync() => inner.GetAllAsync();
        public Task SetAsync(string key, string value) => inner.SetAsync(key, value);
    }

    public RemoteSessionUpdatesTests()
    {
        var builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
        var preferences = new InMemoryUserPreferenceRepository();
        builder.SessionRepository.Seed(new WeaveFleet.Domain.Entities.Session
        {
            Id = "s-asker", Title = "Asker", Status = "active", Directory = "/tmp", CreatedAt = "2026-10-08",
            RetentionStatus = "active", HarnessType = "opencode",
        });
        builder.Build();
        _sender = new SessionUpdateSender(
            new SessionMessagesFeature(new FleetOptions(), new CountingPreferences(preferences, () => _switchReads++)),
            builder.SessionRepository,
            new FakeSessionMessageProxy(),
            builder.Prompting,
            builder.EventBroadcaster,
            new RemoteSessions(_atlas.Service, _atlas));

        var machine = _atlas.Machines.GetAsync(FakeMachine.Id).GetAwaiter().GetResult()!;
        _atlas.Machines.UpsertAsync(machine with { AgentWork = WeaveFleet.Domain.Entities.MachineAgentWork.Always }).GetAwaiter().GetResult();
        _atlas.Answer = request => request == "GET /api/sessions/s-remote/messages"
            ? (HttpStatusCode.OK, """
                {"messages":[
                  {"id":"msg-0","role":"assistant","parts":[{"type":"text","text":"An earlier answer."}]},
                  {"id":"msg-1","role":"user","parts":[{"type":"text","text":"<fleet-session-message …>"}]},
                  {"id":"msg-2","role":"assistant","parts":[{"type":"text","text":"41 passed, "},{"type":"reasoning","text":"…"},{"type":"text","text":"2 failed."}]}
                ],"pagination":{"hasMore":false}}
                """)
            : null;
    }

    public void Dispose() => _atlas.Dispose();

    [Fact]
    public async Task An_update_from_another_machine_is_sent_with_the_session_messages_switch_off()
    {
        // Session messages are off by default; hand-off to a machine has its own switch and asked for this update.
        var remote = new SessionUpdate("s-asker", "s-remote", "user-1", "text", SessionUpdates.Finished, FromOtherMachine: true);
        var local = remote with { FromOtherMachine = false };

        await _sender.SendAsync(local, CancellationToken.None);
        _switchReads.ShouldBe(1);

        await _sender.SendAsync(remote, CancellationToken.None);
        _switchReads.ShouldBe(1);
    }

    [Fact]
    public async Task The_reply_after_the_message_is_read_there_and_names_the_machine()
    {
        var update = await _sender.ReadAsync(Watch, failure: null, CancellationToken.None);

        update.ShouldNotBeNull();
        update.Outcome.ShouldBe(SessionUpdates.Finished);
        update.Text.ShouldBe(SessionMessages.WrapUpdate("s-remote", "Run the integration tests", "finished", "41 passed, 2 failed.", Atlas));
        update.Text.ShouldStartWith("<fleet-session-update session=\"s-remote\" title=\"Run the integration tests\" outcome=\"finished\" machine=\"machine-atlas\" machine-name=\"atlas\">");
        _atlas.Requests.ShouldHaveSingleItem().Token.ShouldBe(FakeMachine.Token);
    }

    [Fact]
    public async Task Each_message_gets_the_reply_of_its_own_turn_and_a_superseded_one_gets_none()
    {
        _atlas.Answer = request => request == "GET /api/sessions/s-remote/messages"
            ? (HttpStatusCode.OK, """
                {"messages":[
                  {"id":"msg-1","role":"user","parts":[{"type":"text","text":"one"}]},
                  {"id":"msg-2","role":"assistant","parts":[{"type":"text","text":"Reply to one."}]},
                  {"id":"msg-3","role":"user","parts":[{"type":"text","text":"two"}]},
                  {"id":"msg-4","role":"user","parts":[{"type":"text","text":"three"}]},
                  {"id":"msg-5","role":"assistant","parts":[{"type":"text","text":"Reply to two and three."}]}
                ],"pagination":{"hasMore":false}}
                """)
            : null;

        (await _sender.ReadAsync(Watch, null, CancellationToken.None))!.Text.ShouldContain("Reply to one.");
        (await _sender.ReadAsync(Watch with { MessageId = "msg-3" }, null, CancellationToken.None)).ShouldBeNull();
        (await _sender.ReadAsync(Watch with { MessageId = "msg-4" }, null, CancellationToken.None))!.Text.ShouldContain("Reply to two and three.");
    }

    [Fact]
    public async Task A_reply_cut_short_says_where_to_read_the_rest()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 400));
        _atlas.Answer = request => request == "GET /api/sessions/s-remote/messages"
            ? (HttpStatusCode.OK, $$$"""
                {"messages":[
                  {"id":"msg-1","role":"user","parts":[{"type":"text","text":"one"}]},
                  {"id":"msg-2","role":"assistant","parts":[{"type":"text","text":"{{{text}}}"}]}
                ],"pagination":{"hasMore":false}}
                """)
            : null;

        var update = await _sender.ReadAsync(Watch, null, CancellationToken.None);

        update!.Text.ShouldContain("fleet_session_read");
    }

    [Fact]
    public async Task A_failed_turn_says_why_without_asking_the_machine()
    {
        var update = await _sender.ReadAsync(Watch, new TurnError { Name = "RateLimit", Message = "Rate limited." }, CancellationToken.None);

        update!.Outcome.ShouldBe(SessionUpdates.Failed);
        update.Text.ShouldContain("Rate limited.");
        _atlas.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_machine_that_doesnt_answer_still_gets_the_asker_told()
    {
        _atlas.Away = true;

        var update = await _sender.ReadAsync(Watch, failure: null, CancellationToken.None);

        update!.Text.ShouldContain("It finished, but Fleet couldn't read its reply: atlas didn't answer.");
    }
}
