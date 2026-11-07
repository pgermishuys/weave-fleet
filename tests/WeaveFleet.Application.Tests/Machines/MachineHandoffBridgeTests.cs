using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
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
/// naming the calling session as the sender. Each machine's setting decides: Always goes ahead, Ask has Fleet ask the user
/// in the conversation and wait for the answer, Never keeps the machine out of reach.
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
    private readonly FakeTimeProvider _time = new();
    private readonly FleetSessionEvents _events = new();
    private readonly Channel<HarnessEvent> _raised = Channel.CreateUnbounded<HarnessEvent>();
    private readonly MachineHandoffAsks _asks;
    private readonly SessionOrchestratorBuilder _builder;
    private readonly MachineHandoffBridge _bridge;

    public MachineHandoffBridgeTests()
    {
        Set(MachineAgentWork.Always);
        _events.Attach(Sender, _raised.Writer);
        _asks = new MachineHandoffAsks(_events, new SessionActivityTracker(), _time);

        _identity = new MachineIdentityStore(_identityPath);
        _identity.Update(identity => identity with { Name = "kestrel" });

        var builder = _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext(UserId));
        builder.SessionRepository.Seed(new Session
        {
            Id = Sender, Title = "Fix login flake", Status = "active", Directory = "/tmp", CreatedAt = "2026-10-08",
            RetentionStatus = "active", HarnessType = "opencode",
        });
        builder.Build();
        var sessions = new SessionService(builder.SessionRepository, builder.ProjectRepository, builder.Retention, builder.ActivityTracker);
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
            sessions,
            builder.SessionRepository,
            _preferences,
            _asks,
            new RemoteSessions(_atlas.Service, _atlas),
            _identity,
            _watches,
            _followed,
            new RecentSessionStarts());

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
    public async Task A_start_repeated_after_a_timeout_gives_back_the_first_session_not_a_second()
    {
        var first = await _bridge.StartAsync("token", "oc-1", "atlas", "/srv/harbor-api", "Run the tests", "Run them.", null, null);
        var retry = await _bridge.StartAsync("token", "oc-1", "atlas", "/srv/harbor-api", "Run the tests", "Run them.", null, null);

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        retry.IsSuccess.ShouldBeTrue(retry.Error?.Message);
        _atlas.Requests.Count(request => request.Request == "POST /api/sessions").ShouldBe(1);
        _atlas.Requests.Count(request => request.Request == "POST /api/machine/peer/sessions/s-remote/message").ShouldBe(1);
        retry.Value!.Output.ShouldBe(first.Value!.Output);
    }

    [Fact]
    public async Task A_second_start_with_another_task_is_a_new_session()
    {
        await _bridge.StartAsync("token", "oc-1", "atlas", "/srv/harbor-api", "Run the tests", "Run them.", null, null);
        await _bridge.StartAsync("token", "oc-1", "atlas", "/srv/harbor-api", "Run the tests", "Run the slow ones.", null, null);

        _atlas.Requests.Count(request => request.Request == "POST /api/sessions").ShouldBe(2);
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
    public async Task A_machine_set_to_Never_is_out_of_reach()
    {
        Set(MachineAgentWork.Never);

        var start = await _bridge.StartAsync("token", "oc-1", "atlas", "/srv/harbor-api", "Run the tests", "Run them.", null, null);
        var list = await _bridge.ListAsync("token", "oc-1");

        start.Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
        start.Error.Message.ShouldBe("You can't hand work to a machine called atlas. See the machines you can with fleet_machine_list.");
        list.Value!.Output.ShouldBe(MachineHandoffBridge.NoMachinesMessage);
        _atlas.Requests.ShouldBeEmpty();
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

    [Fact]
    public async Task On_a_machine_set_to_Ask_the_user_is_asked_in_the_conversation_and_the_start_waits_for_them()
    {
        Set(MachineAgentWork.Ask);
        _atlas.Answer = WithLevels(_atlas.Answer!);

        var start = StartTestsAsync();
        var ask = await AskedAsync();

        start.IsCompleted.ShouldBeFalse();
        _atlas.Requests.ShouldNotContain(request => request.Request == "POST /api/sessions");
        ask.Id.ShouldStartWith(MachineHandoffAsks.IdPrefix);
        ask.SessionId.ShouldBe(Sender);
        ask.Title.ShouldBe("Start a session on atlas");
        ask.Handoff.ShouldBe(new HandoffAskDetails
        {
            Machine = "atlas", Folder = "/srv/harbor-api", Branch = "fix/login-flake", Harness = "opencode",
            Task = "Run them on Windows.", Level = PermissionLevels.Edits,
        });

        _asks.Answer(Sender, ask.Id, PermissionReplies.Once).ShouldBe((FakeMachine.Id, "atlas"));
        var result = await start;

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        _atlas.Requests.Count(request => request.Request == "POST /api/sessions").ShouldBe(1);
    }

    [Fact]
    public async Task Not_the_machine_tells_the_agent_the_user_said_no()
    {
        Set(MachineAgentWork.Ask);

        var start = StartTestsAsync();
        var ask = await AskedAsync();
        _asks.Answer(Sender, ask.Id, PermissionReplies.Reject);
        var result = await start;

        result.Error!.Kind.ShouldBe(CanvasErrorKind.Refused);
        result.Error.Message.ShouldStartWith("The user said no to sending work to atlas.");
        _atlas.Requests.ShouldNotContain(request => request.Request == "POST /api/sessions");
        MachineHandoffAsks.SettingFor(PermissionReplies.Reject).ShouldBe(MachineAgentWork.Never);
        MachineHandoffAsks.SettingFor(PermissionReplies.Always).ShouldBe(MachineAgentWork.Always);
        MachineHandoffAsks.SettingFor(PermissionReplies.Once).ShouldBeNull();
    }

    [Fact]
    public async Task A_round_without_an_answer_says_to_call_again_and_the_call_again_finds_the_same_ask()
    {
        Set(MachineAgentWork.Ask);

        var first = StartTestsAsync();
        var ask = await AskedAsync();
        await Task.Run(() => _time.Advance(MachineHandoffAsks.Round));
        var waiting = await first;

        waiting.Value!.Choice.ShouldBe(PageChoiceOutput.StillWaiting);
        waiting.Value.Output.ShouldBe(MachineHandoffBridge.StillWaitingMessage);

        var again = StartTestsAsync();
        _asks.Answer(Sender, ask.Id, PermissionReplies.Always).ShouldNotBeNull();
        (await again).IsSuccess.ShouldBeTrue();
        var after = new List<string>();
        while (_raised.Reader.TryRead(out var next))
            after.Add(next.Type);
        after.ShouldBe([EventTypes.SessionStatus, EventTypes.PermissionReplied], "waiting on the user, then the card goes; one ask, not two");
    }

    [Fact]
    public async Task A_run_nobody_watches_follows_the_unattended_setting()
    {
        Set(MachineAgentWork.Ask);
        (await _builder.SessionRepository.GetByIdAsync(Sender))!.SourceReference = "automation:nightly";

        (await StartTestsAsync()).IsSuccess.ShouldBeTrue("Unset, nothing asks in an unattended run.");

        _preferences.Seed(SessionPermissions.UnattendedKey, UnattendedPermissions.Deny);
        var refused = await _bridge.StartAsync("token", "oc-1", "atlas", "/srv/harbor-api", "Run the tests", "Another one.", null, null);
        refused.Error!.Message.ShouldContain("this run has nobody watching it");
        _raised.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Messaging_a_session_the_agent_started_there_doesnt_ask_but_another_one_does()
    {
        Set(MachineAgentWork.Ask);
        var caller = new HarnessCanvasCaller(Sender, UserId);
        var start = StartTestsAsync();
        _asks.Answer(Sender, (await AskedAsync()).Id, PermissionReplies.Once);
        await start;

        (await _bridge.MessageAsync(caller, "atlas", "s-remote", "Also the Windows path.", notifyWhenDone: false)).IsSuccess.ShouldBeTrue();

        var other = _bridge.MessageAsync(caller, "atlas", "s-other", "Stop the build.", notifyWhenDone: false);
        var ask = await AskedAsync();
        ask.Title.ShouldBe("Message a session on atlas");
        ask.Handoff!.Session.ShouldBe("s-other");
        ask.Handoff.Task.ShouldBe("Stop the build.");
        _asks.EndAll(Sender);
        (await other).Error!.Message.ShouldStartWith("The user didn't answer about atlas");
    }

    [Fact]
    public async Task The_list_says_which_machines_ask_first()
    {
        Set(MachineAgentWork.Ask);

        var result = await _bridge.ListAsync("token", "oc-1");

        result.Value!.Output.ShouldContain("atlas · Fleet asks the user before you send it work · answering");
    }

    /// <summary>The machine reports its permission levels: Edits for OpenCode, as a newer Fleet does.</summary>
    private static Func<string, (HttpStatusCode, string)?> WithLevels(Func<string, (HttpStatusCode, string)?> answer)
        => request => request == "GET /api/machine"
            ? (HttpStatusCode.OK, """{"id":"machine-atlas","capabilities":{"harnesses":[{"type":"opencode","available":true,"enabled":true,"permissionLevel":"edits"}],"sessions":{"working":0,"needsYou":0},"peerMessages":true,"permissionLevel":"ask"}}""")
            : answer(request);

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

    private void Set(string agentWork)
    {
        var machine = _atlas.Machines.GetAsync(FakeMachine.Id).GetAwaiter().GetResult()!;
        _atlas.Machines.UpsertAsync(machine with { AgentWork = agentWork }).GetAwaiter().GetResult();
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>The next ask Fleet raised in the calling session's conversation.</summary>
    private async Task<PermissionAsk> AskedAsync()
    {
        while (true)
        {
            var raised = await _raised.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (raised.Type == EventTypes.PermissionAsked)
                return raised.Payload!.Value.Deserialize<PermissionAsk>(WebJson)!;
        }
    }

    private Task<WeaveFleet.Application.Canvases.CanvasResult<CanvasToolOutput>> StartTestsAsync()
        => _bridge.StartAsync("token", "oc-1", "atlas", "/srv/harbor-api", "Run the tests", "Run them on Windows.\nThen report.", "fix/login-flake", "opencode");

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
