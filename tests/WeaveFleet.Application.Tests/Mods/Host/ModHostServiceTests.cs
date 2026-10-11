using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Application.Tests.Mods.Host;

public sealed class ModHostServiceTests : IDisposable
{
    private const string Local = "local-user";
    private const string Other = "test-other";

    private readonly FakeFactory _factory = new();
    private readonly InMemoryModVersionStore _store = new();
    private readonly ChannelBroadcaster _events = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ModHostService _service;

    public ModHostServiceTests()
        => _service = new ModHostService(
            new ModHostOptions(), _factory, new FakeGate(), new FakeBun(), new FakeFiles(), _store, new FakeStrikes(), new FakeUi(),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new NoUserScope(), _events, _time, NullLogger<ModHostService>.Instance);

    public void Dispose()
    {
        _service.DisposeAsync().AsTask().Within().GetAwaiter().GetResult();
        _store.DeleteFolders();
    }

    private void Keep(string user)
        => _store.SeedHistory(user, new ModHistory("test-chips", 1, null, [new ModVersion(1, DateTimeOffset.UnixEpoch, "0.1.0", "abc", null, null, null, null)]));

    private static string KeyOf(string user) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16].ToLowerInvariant();

    [Fact]
    public async Task Start_brings_up_the_local_user_without_waiting_for_it()
    {
        Keep(Local);
        _factory.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await _service.StartAsync(CancellationToken.None).Within();
        _factory.Hold.SetResult();
        await _factory.NextStart();
        _factory.Launches.Single().UserKey.ShouldBe(KeyOf(Local));
    }

    [Fact]
    public async Task Mods_changed_for_a_user_ensures_that_users_host()
    {
        Keep(Other);
        await _service.StartAsync(CancellationToken.None).Within();
        _events.PublishModsChanged(Other);
        await _factory.NextStart();
        // The factory is called before the supervisor marks itself running; this waits for that reconcile to finish.
        await _service.EnsureAsync(Other).Within();
        _factory.Launches.Single().UserKey.ShouldBe(KeyOf(Other));
        _service.GetStatus(Other).State.ShouldBe(ModHostStates.Running);
        _service.GetStatus(Local).State.ShouldBe(ModHostStates.Stopped);
    }

    [Fact]
    public async Task Stop_shuts_every_host_down_and_nothing_restarts_afterwards()
    {
        Keep(Local);
        Keep(Other);
        await _service.StartAsync(CancellationToken.None).Within();
        _events.PublishModsChanged(Other);
        var first = await _factory.NextStart();
        var second = await _factory.NextStart();

        await _service.StopAsync(CancellationToken.None).Within();
        first.Shutdowns.Count.ShouldBe(1);
        second.Shutdowns.Count.ShouldBe(1);

        await _service.EnsureAsync(Local).Within();
        _events.PublishModsChanged(Other);
        _time.Advance(TimeSpan.FromMinutes(1));
        _factory.Launches.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Stop_while_a_host_waits_to_restart_cancels_the_restart()
    {
        Keep(Local);
        await _service.StartAsync(CancellationToken.None).Within();
        var connection = await _factory.NextStart();
        var restarting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.SupervisorOf(Local)!.Changed += s =>
        {
            if (s.State == ModHostStates.Restarting)
                restarting.TrySetResult();
        };
        connection.Crash();
        await restarting.Task.Within();

        await _service.StopAsync(CancellationToken.None).Within();
        _time.Advance(TimeSpan.FromMinutes(1));
        _factory.Launches.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Stop_then_dispose_twice_is_harmless()
    {
        Keep(Local);
        await _service.StartAsync(CancellationToken.None).Within();
        var connection = await _factory.NextStart();

        await _service.StopAsync(CancellationToken.None).Within();
        await _service.StopAsync(CancellationToken.None).Within();
        await _service.DisposeAsync().AsTask().Within();
        await _service.DisposeAsync().AsTask().Within();
        connection.Shutdowns.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Mods_changed_clears_the_users_load_problems_and_each_call_goes_to_the_users_host()
    {
        Keep(Other);
        _factory.Answers["load"] = _ => Task.FromException<JsonElement>(new ModHostRpcException(ModHostErrorCodes.NotLoaded, "test-chips doesn't load"));
        await _service.EnsureAsync(Other).Within();
        var connection = await _factory.NextStart();
        _service.GetLoadProblem(Other, "test-chips@v1")!.Message.ShouldBe("test-chips doesn't load");
        _service.GetLoadProblem(Local, "test-chips@v1").ShouldBeNull();
        (await _service.DispatchAsync(Local, new ModDispatchRequest("ui.press", "ses_test1", default)).Within()).ShouldBe(ModDispatchResult.NotDispatched);

        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Answers["load"] = _ =>
        {
            retried.TrySetResult();
            return Task.FromResult(JsonSerializer.SerializeToElement(new { hooks = Array.Empty<object>() }));
        };
        await _service.StartAsync(CancellationToken.None).Within();
        _events.PublishModsChanged(Other);
        await retried.Task.Within();
        _service.GetLoadProblem(Other, "test-chips@v1").ShouldBeNull();

        await _service.ForgetSessionAsync(Other, "ses_test1").Within();
        connection.Sent("forget").Length.ShouldBe(1);
        _factory.Answers["check"] = _ => Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
        (await _service.CheckAsync(Local, "/work/demo-mod").Within()).GetProperty("ok").GetBoolean().ShouldBeTrue();
        (await _factory.NextStart()).Methods.ShouldBe(["check"]);
    }
}
