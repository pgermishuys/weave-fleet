using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Sessions.Asks;
using WeaveFleet.Application.Tests.Mods.Host;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;
using static WeaveFleet.Application.Tests.Mods.ModPromptHookTests;

namespace WeaveFleet.Application.Tests.Mods;

/// <summary>The <c>permission.ask</c> hook fails open: whatever goes wrong, the ask goes to the user.</summary>
public sealed class ModPermissionHookTests : IDisposable
{
    private readonly ScriptedHost _host = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeHarnessSession _harness = new("instance-1");
    private readonly InstanceTracker _tracker = new();
    private readonly ModPermissionHook _hook;

    private static readonly PermissionAsk Ask = new()
    {
        Id = "per_1",
        SessionId = "ses_1",
        Kind = PermissionKinds.Shell,
        Tool = "bash",
        Title = "git push origin main",
        Always = ["git push *"],
    };

    public ModPermissionHookTests()
    {
        var sessions = new InMemorySessionRepository();
        sessions.Seed(new Session { Id = "ses_1", InstanceId = "instance-1", UserId = "user-1", HarnessType = "opencode" });
        _tracker.Register(_harness.InstanceId, _harness);
        // Answering a permission ask never wakes a session, so the activation SessionAsks is given stays unused.
        var asks = new SessionAsks(sessions, _tracker, null!, NullLogger<SessionAsks>.Instance);
        var scopes = TestServiceScopeFactory.Create(services => services.AddSingleton(asks));
        _hook = new ModPermissionHook(_host, scopes, new NoUserScope(), _time, NullLogger<ModPermissionHook>.Instance);
    }

    public void Dispose() => _harness.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private Task<string?> Refusal() => _hook.RefusalAsync("user-1", "ses_1", Ask, CancellationToken.None).Within();

    private static ModDispatchResult Answer(string json) => new(true, JsonDocument.Parse(json).RootElement.Clone(), []);

    [Fact]
    public async Task Sends_what_the_ask_is_and_returns_the_mods_reason_with_its_name()
    {
        _host.Reply = _ => Task.FromResult(Answer("""{"decision":"deny","reason":"Don't push from main.","mod":"push-guard"}"""));

        (await Refusal()).ShouldBe("push-guard: Don't push from main.");
        var (user, request) = _host.Requests.ShouldHaveSingleItem();
        (user, request.Event, request.SessionId).ShouldBe(("user-1", "permission.ask", "ses_1"));
        request.E.GetRawText().ShouldBe("""{"sessionId":"ses_1","requestId":"per_1","kind":"shell","tool":"bash","title":"git push origin main","patterns":["git push *"]}""");
    }

    [Fact]
    public async Task A_reason_without_a_mod_name_and_a_long_one_are_still_passed_on_capped()
    {
        _host.Reply = _ => Task.FromResult(Answer($$"""{"decision":"deny","reason":"{{new string('x', 600)}}"}"""));

        var refusal = (await Refusal()).ShouldNotBeNull();
        refusal.ShouldStartWith("A mod: xxx");
        refusal.Length.ShouldBe("A mod: ".Length + ModPermissionHook.MaxReasonLength);
    }

    [Fact]
    public async Task No_mod_hooking_it_means_the_ask_goes_to_the_user()
    {
        (await Refusal()).ShouldBeNull();
    }

    [Fact]
    public async Task A_host_that_throws_leaves_the_ask_to_the_user()
    {
        _host.Reply = _ => throw new InvalidOperationException("pipe closed");

        (await Refusal()).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"decision":"ask"}""")]
    [InlineData("""{"decision":"allow"}""")]
    [InlineData("""{"decision":"deny"}""")]
    [InlineData("""{"decision":"deny","reason":"  "}""")]
    [InlineData("""{"decision":"deny","reason":5}""")]
    [InlineData("""{"reason":"no"}""")]
    [InlineData("""[1]""")]
    [InlineData("""null""")]
    public async Task Anything_but_a_deny_with_a_reason_leaves_the_ask_to_the_user(string result)
    {
        _host.Reply = _ => Task.FromResult(Answer(result));

        (await Refusal()).ShouldBeNull();
    }

    [Fact]
    public async Task Waiting_longer_than_2_seconds_stops_waiting_and_the_ask_goes_to_the_user()
    {
        var never = new TaskCompletionSource<ModDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Reply = _ => never.Task;

        var pending = Refusal();
        pending.IsCompleted.ShouldBeFalse();
        _time.Advance(ModPermissionHook.Wait - TimeSpan.FromMilliseconds(1));
        pending.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(1));

        (await pending).ShouldBeNull();
        never.SetException(new InvalidOperationException("late"));
    }

    [Fact]
    public async Task Rejecting_answers_the_ask_with_the_message_through_the_shared_reply_path()
    {
        _harness.PendingPermissions.Add("per_1");

        await _hook.RejectAsync("user-1", Ask, "push-guard: Don't push from main.").Within();

        _harness.PermissionReplies.ShouldBe([("per_1", PermissionReplies.Reject, "push-guard: Don't push from main.")]);
    }

    [Fact]
    public async Task Rejecting_an_ask_the_harness_no_longer_has_does_not_throw()
    {
        await _hook.RejectAsync("user-1", Ask, "push-guard: no").Within();

        _harness.PermissionReplies.ShouldBeEmpty();
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
