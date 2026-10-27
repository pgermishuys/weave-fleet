using System.Net;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>How a job reads a session on another machine: a session that's gone frees its seat, a machine that doesn't answer says nothing.</summary>
public sealed class RemoteSessionStateTests : IDisposable
{
    private readonly FakeMachine _atlas = new();
    private string _session = """{"id":"s1","activityStatus":"idle","retentionStatus":"active"}""";
    private string _messages = """{"messages":[]}""";

    public RemoteSessionStateTests()
    {
        _atlas.Answer = request => request switch
        {
            "GET /api/sessions/s1" => (HttpStatusCode.OK, _session),
            "GET /api/sessions/s1/messages" => (HttpStatusCode.OK, _messages),
            _ => null,
        };
    }

    public void Dispose() => _atlas.Dispose();

    private async Task<RemoteSessionState?> StateAsync(string sessionId = "s1")
    {
        var machine = (await _atlas.Machines.GetAsync(FakeMachine.Id))!;
        return await new RemoteSessions(_atlas.Service, _atlas).StateAsync(machine, _atlas.Service.TokenOf(machine), sessionId, CancellationToken.None);
    }

    [Fact]
    public async Task A_session_the_machine_does_not_have_is_gone()
    {
        (await StateAsync("s-deleted")).ShouldBe(RemoteSessionState.Gone);
    }

    [Fact]
    public async Task An_archived_session_is_gone()
    {
        _session = """{"id":"s1","activityStatus":"idle","retentionStatus":"archived"}""";

        (await StateAsync()).ShouldBe(RemoteSessionState.Gone);
    }

    [Fact]
    public async Task A_machine_that_does_not_answer_says_nothing()
    {
        _atlas.Away = true;

        (await StateAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task A_session_in_a_turn_or_waiting_is_read_as_such()
    {
        _session = """{"id":"s1","activityStatus":"busy"}""";
        (await StateAsync()).ShouldBe(RemoteSessionState.InTurn);

        _session = """{"id":"s1","activityStatus":"waiting_input"}""";
        (await StateAsync()).ShouldBe(RemoteSessionState.WaitingInput);
    }

    [Fact]
    public async Task An_idle_session_is_quiet_until_it_has_answered()
    {
        (await StateAsync()).ShouldBe(RemoteSessionState.Quiet);

        _messages = """{"messages":[{"id":"m1","role":"user","parts":[]},{"id":"m2","role":"assistant","finish":"stop","parts":[{"type":"text","text":"Done."}]}]}""";
        (await StateAsync()).ShouldBe(RemoteSessionState.Replied);
    }
}
