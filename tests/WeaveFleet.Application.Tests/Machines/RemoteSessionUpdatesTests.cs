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

    public RemoteSessionUpdatesTests()
    {
        var builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
        var preferences = new InMemoryUserPreferenceRepository();
        _sender = new SessionUpdateSender(
            new SessionMessagesFeature(new FleetOptions(), preferences),
            builder.SessionRepository,
            new FakeSessionMessageProxy(),
            builder.Build(),
            builder.EventBroadcaster,
            new RemoteSessions(_atlas.Service, _atlas));

        var machine = _atlas.Machines.GetAsync(FakeMachine.Id).GetAwaiter().GetResult()!;
        _atlas.Machines.UpsertAsync(machine with { AgentsAllowed = true }).GetAwaiter().GetResult();
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
