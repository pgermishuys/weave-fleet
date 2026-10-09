using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>
/// An agent hands work to another machine: this Fleet starts, messages and reads sessions there with the token it keeps,
/// naming the calling session as the sender. Only machines the owner allowed, only with the switch on.
/// </summary>
public sealed class MachineHandoffBridgeTests : IDisposable
{
    private const string Sender = "s-sender";
    private const string UserId = "user-1";

    private readonly FakeMachine _atlas = new();
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly FakeResolver _resolver = new() { Caller = new HarnessCanvasCaller(Sender, UserId) };
    private readonly string _identityPath = Path.Combine(Path.GetTempPath(), $"fleet-handoff-{Guid.NewGuid():N}.db");
    private readonly MachineIdentityStore _identity;
    private readonly CapturingSender _updates = new();
    private readonly FollowedSessions _followed = new();
    private readonly SessionUpdates _watches;
    private readonly MachineHandoffBridge _bridge;

    public MachineHandoffBridgeTests()
    {
        _preferences.Seed(SessionMessages.PreferenceKey, "true");
        _preferences.Seed(AgentHandoff.PreferenceKey, "true");
        Allow(true);

        _identity = new MachineIdentityStore(_identityPath);
        _identity.Update(identity => identity with { Name = "kestrel" });

        var builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext(UserId));
        builder.SessionRepository.Seed(new Session
        {
            Id = Sender, Title = "Fix login flake", Status = "active", Directory = "/tmp", CreatedAt = "2026-10-08",
            RetentionStatus = "active", HarnessType = "opencode",
        });
        var sessions = new SessionService(builder.SessionRepository, builder.ProjectRepository, builder.Build(), builder.ActivityTracker);
        var options = new FleetOptions();
        _watches = new SessionUpdates(
            new SessionActivityTracker(),
            TestServiceScopeFactory.Create(services =>
            {
                services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
                services.AddSingleton<ISessionUpdateSender>(_updates);
            }),
            NullLogger<SessionUpdates>.Instance);
        _bridge = new MachineHandoffBridge(
            [_resolver],
            new NoUserScope(),
            new AgentHandoffFeature(options, _preferences, new SessionMessagesFeature(options, _preferences)),
            sessions,
            new RemoteSessions(_atlas.Service, _atlas),
            _identity,
            _watches,
            _followed);

        _atlas.Answer = request => request switch
        {
            "GET /api/machine" => (HttpStatusCode.OK, """{"id":"machine-atlas","capabilities":{"harnesses":[{"type":"opencode","available":true,"enabled":true},{"type":"pi","available":false,"enabled":true}],"sessions":{"working":0,"needsYou":0},"peerMessages":true}}"""),
            "GET /api/repositories" => (HttpStatusCode.OK, """{"repositories":[{"path":"/srv/harbor-api","name":"harbor-api"}],"scannedAt":0}"""),
            "POST /api/sessions" => (HttpStatusCode.OK, """{"instanceId":"i1","workspaceId":"w1","session":{"id":"s-remote","title":"Run the integration tests"},"branch":"fix/login-flake"}"""),
            "POST /api/machine/peer/sessions/s-remote/message" => (HttpStatusCode.OK, """{"sessionId":"s-remote","title":"Run the integration tests","messageId":"msg-1"}"""),
            "GET /api/machine/peer/sessions/s-remote/page" => (HttpStatusCode.OK, """{"title":"Read Run the integration tests","text":"Session \"Run the integration tests\" (s-remote) …"}"""),
            _ => null,
        };
    }

    public void Dispose()
    {
        _atlas.Dispose();
        File.Delete(_identityPath);
        File.Delete(Path.ChangeExtension(_identityPath, ".machine.json"));
    }

    [Fact]
    public async Task Starting_makes_an_empty_session_on_the_pushed_branch_then_gives_it_the_task_from_this_session()
    {
        var result = await _bridge.StartAsync("token", "oc-1", "ATLAS", "/srv/harbor-api", "Run the integration tests", "Run them on fix/login-flake.", "fix/login-flake", "");

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value!.Title.ShouldBe("Started Run the integration tests on atlas");
        result.Value!.Output.ShouldContain("(s-remote) in a worktree on fix/login-flake");

        var start = Body("POST /api/sessions");
        start.GetProperty("directory").GetString().ShouldBe("/srv/harbor-api");
        start.TryGetProperty("initialPrompt", out _).ShouldBeFalse();
        start.TryGetProperty("harnessType", out _).ShouldBeFalse();
        var input = start.GetProperty("source").GetProperty("input");
        input.GetProperty("isolationStrategy").GetString().ShouldBe("worktree");
        input.GetProperty("branch").GetString().ShouldBe("fix/login-flake");
        input.GetProperty("baseBranch").GetString().ShouldBe("origin/fix/login-flake");

        var message = Body("POST /api/machine/peer/sessions/s-remote/message");
        message.GetProperty("fromMachineId").GetString().ShouldBe(_identity.Get().Id);
        message.GetProperty("fromMachineName").GetString().ShouldBe("kestrel");
        message.GetProperty("fromSessionId").GetString().ShouldBe(Sender);
        message.GetProperty("fromTitle").GetString().ShouldBe("Fix login flake");
        message.GetProperty("text").GetString().ShouldBe("Run them on fix/login-flake.");
        _atlas.Requests.ShouldAllBe(request => request.Token == FakeMachine.Token);
    }

    [Fact]
    public async Task Without_a_branch_the_session_works_in_the_folder_as_it_is()
    {
        await _bridge.StartAsync("token", "oc-1", "atlas", "/srv/harbor-api", "Tidy the notes", "Tidy them.", null, "opencode");

        var start = Body("POST /api/sessions");
        start.GetProperty("isolationStrategy").GetString().ShouldBe("existing");
        start.GetProperty("harnessType").GetString().ShouldBe("opencode");
        start.GetProperty("source").GetProperty("input").GetProperty("directory").GetString().ShouldBe("/srv/harbor-api");
    }

    [Fact]
    public async Task A_machine_on_an_older_Fleet_gets_no_session()
    {
        _atlas.Answer = request => request == "GET /api/machine"
            ? (HttpStatusCode.OK, """{"id":"machine-atlas","capabilities":{"harnesses":[],"sessions":{"working":0,"needsYou":0}}}""")
            : null;

        var result = await _bridge.StartAsync("token", "oc-1", "atlas", "/srv/harbor-api", "Run the tests", "Run them.", null, null);

        result.Error!.Kind.ShouldBe(CanvasErrorKind.Refused);
        result.Error.Message.ShouldBe("Fleet on atlas is too old to take work from an agent here. Update it there.");
        _atlas.Requests.ShouldNotContain(request => request.Request == "POST /api/sessions");
    }

    [Fact]
    public async Task A_machine_the_owner_didnt_allow_is_out_of_reach()
    {
        Allow(false);

        var start = await _bridge.StartAsync("token", "oc-1", "atlas", "/srv/harbor-api", "Run the tests", "Run them.", null, null);
        var list = await _bridge.ListAsync("token", "oc-1");

        start.Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
        start.Error.Message.ShouldBe("You can't hand work to a machine called atlas. See the machines you can with fleet_machine_list.");
        list.Value!.Output.ShouldBe(MachineHandoffBridge.NoMachinesMessage);
        _atlas.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_goes_out_while_the_switch_is_off()
    {
        _preferences.Seed(AgentHandoff.PreferenceKey, "false");

        var result = await _bridge.StartAsync("token", "oc-1", "atlas", "/srv/harbor-api", "Run the tests", "Run them.", null, null);
        var read = await _bridge.ReadAsync(new HarnessCanvasCaller(Sender, UserId), "atlas", "s-remote", null, null);

        result.Error!.Message.ShouldBe(MachineHandoffBridge.TurnedOffMessage);
        read.Error!.Message.ShouldBe(MachineHandoffBridge.TurnedOffMessage);
        _atlas.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task It_needs_messages_between_sessions_too()
    {
        _preferences.Seed(SessionMessages.PreferenceKey, "false");

        var result = await _bridge.ListAsync("token", "oc-1");

        result.Error!.Message.ShouldBe(MachineHandoffBridge.TurnedOffMessage);
    }

    [Fact]
    public async Task The_list_says_what_each_allowed_machine_can_run()
    {
        var result = await _bridge.ListAsync("token", "oc-1");

        result.Value!.Output.ShouldBe("""
            Machines you can hand work to:

            atlas · answering
              Harnesses: opencode
              Folders: /srv/harbor-api
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_machine_that_doesnt_answer_says_so()
    {
        _atlas.Away = true;

        var list = await _bridge.ListAsync("token", "oc-1");
        var start = await _bridge.StartAsync("token", "oc-1", "atlas", "/srv/harbor-api", "Run the tests", "Run them.", null, null);

        list.Value!.Output.ShouldContain("atlas · not answering: atlas didn't answer.");
        start.Error!.Message.ShouldBe("atlas didn't answer.");
    }

    [Fact]
    public async Task A_message_goes_there_as_this_session_and_its_reply_is_read_there()
    {
        var caller = new HarnessCanvasCaller(Sender, UserId);

        var sent = await _bridge.MessageAsync(caller, "atlas", "s-remote", "Also check the Windows path.", notifyWhenDone: false);
        var read = await _bridge.ReadAsync(caller, "atlas", "s-remote", "msg-9", 10);

        sent.Value!.Title.ShouldBe("Messaged Run the integration tests on atlas");
        Body("POST /api/machine/peer/sessions/s-remote/message").GetProperty("fromSessionId").GetString().ShouldBe(Sender);
        read.Value!.Title.ShouldBe("Read Run the integration tests on atlas");
        read.Value!.Output.ShouldStartWith("Session \"Run the integration tests\"");
    }

    [Fact]
    public async Task Asking_to_be_told_watches_the_turn_there_that_answers_the_message()
    {
        var sent = await _bridge.MessageAsync(new HarnessCanvasCaller(Sender, UserId), "atlas", "s-remote", "Tell me which fail.", notifyWhenDone: true);

        sent.Value!.Output.ShouldEndWith("Fleet will send you its reply when it's done, as a new message; you don't need to check on it.");
        _followed.Sessions.ShouldBe([(FakeMachine.Id, "s-remote")]);
        await TurnEndsAsync("s-remote", answering: "msg-1");
        var watch = _updates.Read.ShouldHaveSingleItem();
        watch.ShouldBe(new SessionUpdateWatch(Sender, "s-remote", UserId, "msg-1", new SessionMessageMachine(FakeMachine.Id, FakeMachine.Name), "Run the integration tests"));
    }

    [Fact]
    public async Task A_session_started_there_can_be_watched_from_the_start()
    {
        var result = await _bridge.StartAsync(
            "token", "oc-1", "atlas", "/srv/harbor-api", "Run the integration tests", "Run them.", "fix/login-flake", null, notifyWhenDone: true);

        result.Value!.Output.ShouldEndWith("Fleet will send you its reply when it's done, as a new message; you don't need to check on it.");
        await TurnEndsAsync("s-remote", answering: "msg-1");
        _updates.Read.ShouldHaveSingleItem().Machine!.Id.ShouldBe(FakeMachine.Id);
    }

    [Fact]
    public async Task Without_asking_nothing_is_watched()
    {
        await _bridge.MessageAsync(new HarnessCanvasCaller(Sender, UserId), "atlas", "s-remote", "FYI.", notifyWhenDone: false);

        await TurnEndsAsync("s-remote", answering: "msg-1");
        _updates.Read.ShouldBeEmpty();
        _followed.Sessions.ShouldBeEmpty();
    }

    /// <summary>The session there replies to <paramref name="answering"/> and goes idle, as its events say.</summary>
    private async Task TurnEndsAsync(string sessionId, string answering)
    {
        _watches.Observe(sessionId, new MessageUpdated
        {
            Payload = new MessageLifecyclePayload
            {
                Info = new MessageEventInfo { Id = "reply-1", Role = "assistant", SessionId = sessionId, ParentId = answering, Time = new MessageEventTime { Created = 0 } },
            },
        });
        _watches.Observe(sessionId, new SessionIdled { Payload = new SessionIdledPayload { SessionId = sessionId } });
        await _watches.Pending;
    }

    private void Allow(bool allowed)
    {
        var machine = _atlas.Machines.GetAsync(FakeMachine.Id).GetAwaiter().GetResult()!;
        _atlas.Machines.UpsertAsync(machine with { AgentsAllowed = allowed }).GetAwaiter().GetResult();
    }

    private JsonElement Body(string request)
    {
        var body = _atlas.Requests.Last(r => r.Request == request).Body!;
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private sealed class FakeResolver : IHarnessCanvasCallerResolver
    {
        public HarnessCanvasCaller? Caller { get; set; }

        public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
            => Task.FromResult(Caller);
    }

    private sealed class FollowedSessions : IRemoteSessionEvents
    {
        public List<(string MachineId, string SessionId)> Sessions { get; } = [];

        public void Follow(string machineId, string sessionId) => Sessions.Add((machineId, sessionId));
    }

    private sealed class CapturingSender : ISessionUpdateSender
    {
        public List<SessionUpdateWatch> Read { get; } = [];

        public Task<SessionUpdate?> ReadAsync(SessionUpdateWatch watch, TurnError? failure, CancellationToken ct)
        {
            Read.Add(watch);
            return Task.FromResult<SessionUpdate?>(null);
        }

        public Task<bool> SendAsync(SessionUpdate update, CancellationToken ct) => Task.FromResult(true);
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
