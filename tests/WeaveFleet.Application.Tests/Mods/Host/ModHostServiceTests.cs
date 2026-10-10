using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Tests.Mods.Host;

/// <summary>The service over every user's supervisor: start-up, <c>mods.changed</c>, the poll and shutdown.</summary>
public sealed class ModHostServiceTests : IAsyncLifetime
{
    private const string User = ModHostRig.User;
    private const string Other = "other-user";
    private const string Chips = "test-chips";

    private ModHostRig Rig { get; } = new();
    private readonly ChannelEventBroadcaster _events = new();
    private ModHostService? Built { get; set; }

    private ModHostService Service(params string[] startupUsers) => Built ??= new ModHostService(Rig.Deps, _events, startupUsers);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (Built is not null)
            await Built.StopAsync(CancellationToken.None).WaitAsync(ModHostTests.Patience);
        await Rig.DisposeAsync();
    }

    private Task ModsChangedAsync(string? userId, string type = EventTypes.ModsChanged)
        => _events.BroadcastAsync("sessions", type, JsonDocument.Parse("""{ "reason": "kept" }""").RootElement.Clone(), userId, CancellationToken.None);

    private async Task StartedAsync(ModHostService service)
    {
        await service.StartAsync(CancellationToken.None).Within();
        await ModHostTests.Eventually(() => _events.Subscribers == 1, "the service listens for mods.changed");
    }

    [Fact]
    public void A_user_without_a_host_is_stopped()
    {
        Service().GetStatus(User).ShouldBe(ModHostStatus.StoppedWith(null));
        Service().GetLoadProblem(User, "test-chips@v1").ShouldBeNull();
    }

    [Fact]
    public async Task Start_up_brings_the_local_users_host_up_without_waiting_for_it()
    {
        Rig.Keep(Chips);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Rig.Factory.BeforeStart = _ => release.Task;
        var service = Service(User);

        await service.StartAsync(CancellationToken.None).Within();
        service.GetStatus(User).State.ShouldNotBe(ModHostStates.Running);

        release.SetResult();
        await ModHostTests.Eventually(() => service.GetStatus(User).State == ModHostStates.Running, "the local user's host is running");
    }

    [Fact]
    public async Task Mods_changed_brings_that_users_host_in_line()
    {
        var service = Service();
        await StartedAsync(service);

        Rig.Keep(Chips, user: Other);
        await ModsChangedAsync(Other, type: "session.updated");
        await ModsChangedAsync(null);
        await Task.Delay(50);
        Rig.Factory.Launches.ShouldBeEmpty();

        await ModsChangedAsync(Other);
        await ModHostTests.Eventually(() => service.GetStatus(Other).Loaded.SequenceEqual(["test-chips@v1"]), "the other user's mod loaded");
        service.GetStatus(User).State.ShouldBe(ModHostStates.Stopped);
    }

    [Fact]
    public async Task Mods_changed_tries_a_refused_mod_again()
    {
        Rig.Factory.Refusals["test-chips@v1"] = ("no", null);
        Rig.Keep(Chips);
        var service = Service();
        await StartedAsync(service);
        await service.EnsureAsync(User).Within();
        service.GetLoadProblem(User, "test-chips@v1").ShouldNotBeNull();

        Rig.Factory.Refusals.Clear();
        await ModsChangedAsync(User);

        await ModHostTests.Eventually(() => service.GetStatus(User).Loaded.Count == 1, "the mod loaded");
        service.GetLoadProblem(User, "test-chips@v1").ShouldBeNull();
    }

    [Fact]
    public async Task The_poll_brings_running_and_not_ready_hosts_in_line_every_minute()
    {
        Rig.Keep(Chips);
        Rig.Bun.Location = null;
        var service = Service();
        await StartedAsync(service);
        await service.EnsureAsync(User).Within();
        service.GetStatus(User).State.ShouldBe(ModHostStates.NotReady);

        Rig.Bun.Location = new BunLocation("/bun/1.3.0/bun", "fleet", "1.3.0");
        Rig.Time.Advance(TimeSpan.FromSeconds(60));
        await ModHostTests.Eventually(() => service.GetStatus(User).State == ModHostStates.Running, "the poll started the host");

        Rig.Bun.Location = new BunLocation("/bun/1.4.0/bun", "fleet", "1.4.0");
        Rig.Time.Advance(TimeSpan.FromSeconds(60));
        await ModHostTests.Eventually(() => service.GetStatus(User).BunPath == "/bun/1.4.0/bun", "the poll moved the host to the new Bun");
    }

    [Fact]
    public async Task Each_user_has_their_own_host()
    {
        Rig.Keep(Chips);
        Rig.Keep("demo-mod", user: Other);
        var service = Service();

        await service.EnsureAsync(User).Within();
        await service.EnsureAsync(Other).Within();

        Rig.Factory.Launches.Select(l => l.WorkingDirectory).ShouldBe([Rig.Store.HostFolder(User), Rig.Store.HostFolder(Other)]);
        Rig.Factory.Launches.Select(l => l.UserKey).Distinct().Count().ShouldBe(2);
        service.GetStatus(User).Loaded.ShouldBe(["test-chips@v1"]);
        service.GetStatus(Other).Loaded.ShouldBe(["demo-mod@v1"]);
    }

    [Fact]
    public async Task A_dispatch_for_a_user_seen_for_the_first_time_isnt_dispatched_but_brings_their_host_up()
    {
        Rig.Keep(Chips);
        var service = Service();

        var result = await service.DispatchAsync(User, new ModDispatchRequest("turn.complete", "ses_test1", ModHostTests.Json("{}"))).Within();

        result.ShouldBe(ModDispatchResult.NotDispatched);
        await ModHostTests.Eventually(() => service.GetStatus(User).State == ModHostStates.Running, "the user's host is running");
        service.GetStatus(User).Loaded.ShouldBe(["test-chips@v1"]);
        (await service.DispatchAsync(User, new ModDispatchRequest("turn.complete", "ses_test1", ModHostTests.Json("{}"))).Within()).Dispatched.ShouldBeTrue();
    }

    [Theory]
    [InlineData("status")]
    [InlineData("problem")]
    [InlineData("log")]
    [InlineData("forget")]
    public async Task Any_call_for_a_user_seen_for_the_first_time_brings_their_host_up(string call)
    {
        Rig.Keep(Chips);
        var service = Service();

        switch (call)
        {
            case "status":
                service.GetStatus(User).ShouldBe(ModHostStatus.StoppedWith(null));
                break;
            case "problem":
                service.GetLoadProblem(User, "test-chips@v1").ShouldBeNull();
                break;
            case "log":
                service.GetLog(User, "test-chips@v1").ShouldBeEmpty();
                break;
            default:
                await service.ForgetSessionAsync(User, "ses_test1").Within();
                break;
        }

        await ModHostTests.Eventually(() => service.GetStatus(User).State == ModHostStates.Running, "the user's host is running");
    }

    [Fact]
    public async Task A_user_is_brought_in_line_once_on_first_sight_not_on_every_call()
    {
        var service = Service();

        for (var i = 0; i < 5; i++)
            service.GetStatus(Other);

        await ModHostTests.Eventually(() => Rig.Gate.Asked(Other) == 1, "the first sight reconciled");
        service.GetStatus(Other);
        await service.DispatchAsync(Other, new ModDispatchRequest("turn.complete", "ses_test1", ModHostTests.Json("{}"))).Within();
        await Task.Delay(50);
        Rig.Gate.Asked(Other).ShouldBe(1);
        Rig.Factory.Launches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Calls_reach_the_users_supervisor_and_log()
    {
        Rig.Keep(Chips);
        var service = Service();
        await service.EnsureAsync(User).Within();

        (await service.DispatchAsync(User, new ModDispatchRequest("turn.complete", "ses_test1", ModHostTests.Json("{}"))).Within()).Dispatched.ShouldBeTrue();
        await service.ForgetSessionAsync(User, "ses_test1").Within();
        (await service.CheckAsync(User, "/staged/test-chips").Within()).GetProperty("ok").GetBoolean().ShouldBeTrue();
        Rig.Factory.Current.Calls.HandleNotification("log", ModHostTests.Json("""{ "mod": "test-chips@v1", "level": "info", "text": "hello" }"""));

        Rig.Factory.Current.Forgotten.ShouldBe(["ses_test1"]);
        service.GetLog(User, "test-chips@v1").ShouldHaveSingleItem().Text.ShouldBe("hello");
        service.SupervisorOf(User).ShouldNotBeNull();
    }

    [Fact]
    public async Task Stop_shuts_every_host_down_and_nothing_starts_again()
    {
        Rig.Keep(Chips);
        Rig.Keep("demo-mod", user: Other);
        var service = Service();
        await StartedAsync(service);
        await service.EnsureAsync(User).Within();
        await service.EnsureAsync(Other).Within();

        await service.StopAsync(CancellationToken.None).Within();

        Rig.Factory.Started.ShouldAllBe(c => c.ShutdownGrace == TimeSpan.FromSeconds(2));
        await ModsChangedAsync(User);
        await service.EnsureAsync(User).Within();
        Rig.Time.Advance(TimeSpan.FromMinutes(2));
        await Task.Delay(50);
        Rig.Factory.Started.Count.ShouldBe(2);
        await Should.ThrowAsync<ModHostNotReadyException>(() => service.CheckAsync(User, "/staged/test-chips").Within());
    }

    [Fact]
    public async Task Stopping_and_disposing_more_than_once_is_harmless()
    {
        // The container disposes the one instance once per registration it was handed out under (the service, IModHost
        // and the hosted service), after the host has stopped it.
        Rig.Keep(Chips);
        var service = Service();
        await StartedAsync(service);
        await service.EnsureAsync(User).Within();

        await service.StopAsync(CancellationToken.None).Within();
        await service.DisposeAsync().AsTask().Within();
        await service.DisposeAsync().AsTask().Within();
        await service.StopAsync(CancellationToken.None).Within();

        Rig.Factory.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Draft_sessions_are_looked_up_as_their_user_in_a_scope_of_their_own()
    {
        Rig.Draft("ses_test1", "demo-mod");
        var users = new RecordingUserScope();
        var scopes = TestServiceScopeFactory.Create(s => s.AddScoped<ISessionRepository>(_ => Rig.Sessions));
        Built = new ModHostService(
            Rig.Factory, Rig.Gate, Rig.Recorder, Rig.Ui, Rig.Signals, Rig.Bun, Rig.Files, Rig.Watcher, Rig.Store,
            scopes, users, _events, Rig.Log, Rig.Time, NullLogger<ModHostService>.Instance);

        await Built.EnsureAsync(User).Within();

        Built.GetStatus(User).Loaded.ShouldBe(["demo-mod@draft:ses_test1"]);
        users.Begun.ShouldContain(User);
    }

    private sealed class RecordingUserScope : IBackgroundUserScope
    {
        public List<string> Begun { get; } = [];

        public IDisposable Begin(string userId)
        {
            lock (Begun)
                Begun.Add(userId);
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
