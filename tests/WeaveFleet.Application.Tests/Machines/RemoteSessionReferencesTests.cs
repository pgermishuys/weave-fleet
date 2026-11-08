using System.Net;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>
/// An <c>@</c> reference to a session on another machine: looked up there with the machine's token, with its recap
/// when the harness here needs one, and refused with a reason when the agent here couldn't read it.
/// </summary>
public sealed class RemoteSessionReferencesTests : IDisposable
{
    private readonly FakeMachine _atlas = new();
    private readonly RemoteSessionReferences _sut;

    public RemoteSessionReferencesTests()
    {
        _atlas.Answer = request => request switch
        {
            "GET /api/sessions/s1" => (HttpStatusCode.OK, """{"id":"s1","title":"Tide chart caching","activityStatus":"idle"}"""),
            "GET /api/machine/peer/sessions/s1/recap" => (HttpStatusCode.OK, """{"title":"Tide chart caching","recap":"It cached charts per harbour."}"""),
            _ => null,
        };
        _sut = new RemoteSessionReferences(_atlas.Service, new RemoteSessions(_atlas.Service, _atlas), _atlas.Identity);
    }

    public void Dispose() => _atlas.Dispose();

    [Fact]
    public async Task A_session_there_is_named_with_its_machine_and_title_and_no_recap_unless_asked()
    {
        var found = await _sut.FindAsync(FakeMachine.Id, "s1", withRecap: false);

        found.ShouldBe(new RemoteSessionReference(FakeMachine.Name, "Tide chart caching", null, null));
        _atlas.Requests.ShouldHaveSingleItem().ShouldBe(("GET /api/sessions/s1", FakeMachine.Token, null));
    }

    [Fact]
    public async Task With_a_recap_asked_for_the_machine_sends_it()
    {
        var found = await _sut.FindAsync(FakeMachine.Id, "s1", withRecap: true);

        found.Recap.ShouldBe("It cached charts per harbour.");
    }

    [Fact]
    public async Task An_older_fleet_without_the_recap_endpoint_leaves_a_link_not_an_error()
    {
        _atlas.Answer = request => request == "GET /api/sessions/s1"
            ? (HttpStatusCode.OK, """{"id":"s1","title":"Tide chart caching"}""")
            : null;

        var found = await _sut.FindAsync(FakeMachine.Id, "s1", withRecap: true);

        found.Error.ShouldBeNull();
        found.Title.ShouldBe("Tide chart caching");
        found.Recap.ShouldBeNull();
    }

    [Fact]
    public async Task A_session_the_machine_does_not_have_is_refused()
    {
        var found = await _sut.FindAsync(FakeMachine.Id, "s-gone", withRecap: false);

        found.Error.ShouldBe("names a session atlas doesn't have. Pick it again from the @ list.");
    }

    [Fact]
    public async Task A_machine_that_does_not_answer_is_refused_with_why()
    {
        _atlas.Away = true;

        var found = await _sut.FindAsync(FakeMachine.Id, "s1", withRecap: false);

        found.Error.ShouldNotBeNull();
        found.Error.ShouldStartWith("is a session on atlas, and Fleet on ");
    }

    [Fact]
    public async Task A_machine_not_in_the_list_or_set_to_never_is_refused_without_asking_it()
    {
        var unlisted = await _sut.FindAsync("machine-hangar", "s1", withRecap: false);
        unlisted.Error.ShouldNotBeNull();
        unlisted.Error.ShouldContain("doesn't list");

        var atlas = (await _atlas.Machines.GetAsync(FakeMachine.Id))!;
        await _atlas.Machines.UpsertAsync(atlas with { AgentWork = MachineAgentWork.Never });
        var never = await _sut.FindAsync(FakeMachine.Id, "s1", withRecap: false);
        never.Error.ShouldNotBeNull();
        never.Error.ShouldContain("set to Never");

        _atlas.Requests.ShouldBeEmpty();
    }
}
