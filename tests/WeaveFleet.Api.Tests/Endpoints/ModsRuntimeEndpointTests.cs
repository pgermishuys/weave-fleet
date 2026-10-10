using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Domain.Common;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// The mod runtime's API against a fake Bun installer: what the install, cancel and found routes answer, and that the
/// two that start using Mods turn the switch on. Nothing here reaches the network.
/// </summary>
public sealed class ModsRuntimeEndpointTests : IAsyncDisposable
{
    private readonly ScriptedBunRuntime _bun = new();
    private readonly FakeBunPathSetting _setting = new();
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ModsRuntimeEndpointTests()
    {
        _factory = new ApiWebApplicationFactory(authEnabled: false, configureTestServices: services =>
        {
            services.AddSingleton<IBunRuntime>(_bun);
            services.AddSingleton<IBunPathSetting>(_setting);
        });
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _bun.Release();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private const string Runtime = "/api/features/mods/runtime";

    private async Task<bool> SwitchOnAsync() => (await _client.GetFromJsonAsync<JsonElement>("/api/features/mods")).GetProperty("on").GetBoolean();

    [Fact]
    public async Task The_view_is_there_while_the_switch_is_off()
    {
        var response = await _client.GetAsync(Runtime);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        view.GetProperty("bun").ValueKind.ShouldBe(JsonValueKind.Null);
        view.GetProperty("configuredPath").ValueKind.ShouldBe(JsonValueKind.Null);
        view.GetProperty("configuredInConfig").GetBoolean().ShouldBeFalse();
        view.GetProperty("job").ValueKind.ShouldBe(JsonValueKind.Null);
        view.GetProperty("installedSize").GetInt64().ShouldBe(0);
        var release = view.GetProperty("release");
        release.GetProperty("version").GetString().ShouldBe("1.4.2");
        release.GetProperty("oldestSafe").GetString().ShouldBe("1.4.0");
        release.GetProperty("hasBuild").GetBoolean().ShouldBeTrue();
        release.GetProperty("size").GetInt64().ShouldBeGreaterThan(0);
        release.GetProperty("source").GetString().ShouldBe("github.com/oven-sh/bun");
        (await SwitchOnAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Installing_turns_the_switch_on_and_answers_202_while_it_runs()
    {
        _bun.HoldInstall();

        var response = await _client.PostAsync($"{Runtime}/install", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await SwitchOnAsync()).ShouldBeTrue();
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        view.GetProperty("release").GetProperty("version").GetString().ShouldBe("1.4.2");
        await _bun.InstallStarted.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Installing_again_while_it_runs_joins_it_and_installing_when_done_answers_200()
    {
        _bun.HoldInstall();
        (await _client.PostAsync($"{Runtime}/install", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _bun.InstallStarted.WaitAsync(TimeSpan.FromSeconds(10));

        (await _client.PostAsync($"{Runtime}/install", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _bun.Ensures.ShouldBe(1);

        _bun.Release();
        await WaitForAsync(async () => (await _client.GetFromJsonAsync<JsonElement>(Runtime)).GetProperty("job").GetProperty("phase").GetString() == "succeeded");

        var done = await _client.PostAsync($"{Runtime}/install", null);
        done.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await done.Content.ReadFromJsonAsync<JsonElement>();
        view.GetProperty("bun").GetProperty("version").GetString().ShouldBe("1.4.2");
        view.GetProperty("bun").GetProperty("source").GetString().ShouldBe("installed");
        view.GetProperty("job").GetProperty("kind").GetString().ShouldBe("install");
        _bun.Ensures.ShouldBe(1);
    }

    [Fact]
    public async Task Installing_ends_start_without_mods_like_turning_the_switch_on_does()
    {
        (await _client.PutAsJsonAsync("/api/preferences/Mods", new { value = "true" })).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync("/api/mods/safe-mode", new { on = true })).EnsureSuccessStatusCode();
        (await _client.PutAsJsonAsync("/api/preferences/Mods", new { value = "false" })).EnsureSuccessStatusCode();

        (await _client.PostAsync($"{Runtime}/install", null)).EnsureSuccessStatusCode();

        var mods = await _client.GetFromJsonAsync<JsonElement>("/api/features/mods");
        mods.GetProperty("on").GetBoolean().ShouldBeTrue();
        mods.GetProperty("safeMode").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Installing_is_refused_with_409_while_the_user_has_their_own_bun()
    {
        _setting.Seed("local-user", "/opt/tools/bun/bin/bun");

        var response = await _client.PostAsync($"{Runtime}/install", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .ShouldBe("Fleet uses your own Bun at /opt/tools/bun/bin/bun. Clear it to use Fleet's own.");
        (await SwitchOnAsync()).ShouldBeFalse();
        _bun.Ensures.ShouldBe(0);
    }

    [Fact]
    public async Task Cancelling_with_nothing_running_is_409()
    {
        var response = await _client.PostAsync($"{Runtime}/cancel", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe("No install is running.");
    }

    [Fact]
    public async Task Cancelling_ends_the_install_and_turns_the_switch_back_off()
    {
        _bun.HoldInstall(waitForCancel: true);
        (await _client.PostAsync($"{Runtime}/install", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _bun.InstallStarted.WaitAsync(TimeSpan.FromSeconds(10));
        (await SwitchOnAsync()).ShouldBeTrue();

        var response = await _client.PostAsync($"{Runtime}/cancel", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var job = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("job");
        job.GetProperty("phase").GetString().ShouldBe("failed");
        job.GetProperty("reason").GetString().ShouldBe("cancelled");
        (await SwitchOnAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Found_lists_the_buns_on_the_computer_with_a_display_path()
    {
        _bun.Found = [new BunCandidate("/opt/tools/bun/bin/bun", "/opt/tools/bun/bin/bun", "1.4.5", BunCandidateStatuses.Usable, null)];

        var found = await _client.GetFromJsonAsync<JsonElement>($"{Runtime}/found");

        var candidate = found.GetProperty("candidates").EnumerateArray().Single();
        candidate.GetProperty("path").GetString().ShouldBe("/opt/tools/bun/bin/bun");
        candidate.GetProperty("displayPath").GetString().ShouldNotBeNull();
        candidate.GetProperty("resolvedPath").GetString().ShouldBe("/opt/tools/bun/bin/bun");
        candidate.GetProperty("version").GetString().ShouldBe("1.4.5");
        candidate.GetProperty("status").GetString().ShouldBe("usable");
    }

    [Fact]
    public async Task Saving_a_path_is_refused_with_409_when_configuration_sets_one()
    {
        _setting.FromConfiguration = "/etc/fleet/bun";

        var response = await _client.PutAsJsonAsync($"{Runtime}/bun-path", new { path = "/opt/tools/bun/bin/bun" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .ShouldBe("Fleet's configuration sets Fleet:Harness:BunPath, so it can't be changed here.");
        (await _client.PutAsJsonAsync($"{Runtime}/bun-path", new { path = (string?)null })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_path_the_check_refuses_is_a_400_with_the_checks_message_and_changes_nothing()
    {
        _bun.Checks["/opt/other/bun"] = new BunCandidate("/opt/other/bun", "/opt/other/bun", null, BunCandidateStatuses.NotChecked,
            "Fleet didn't run it: /opt/other/bun is owned by another user.");

        var response = await _client.PutAsJsonAsync($"{Runtime}/bun-path", new { path = "/opt/other/bun" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .ShouldBe("Fleet didn't run it: /opt/other/bun is owned by another user.");
        (await SwitchOnAsync()).ShouldBeFalse();
        _setting.Saves.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_usable_path_is_saved_and_turns_the_switch_on()
    {
        _bun.Checks["/opt/tools/bun/bin/bun"] = new BunCandidate("/opt/tools/bun/bin/bun", "/opt/tools/bun/bin/bun", "1.4.5", BunCandidateStatuses.Usable, null);
        _bun.UserBun["local-user"] = new BunLocation("/opt/tools/bun/bin/bun", BunSources.Configured, "1.4.5");

        var response = await _client.PutAsJsonAsync($"{Runtime}/bun-path", new { path = "/opt/tools/bun/bin/bun" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        view.GetProperty("configuredPath").GetString().ShouldBe("/opt/tools/bun/bin/bun");
        view.GetProperty("bun").GetProperty("source").GetString().ShouldBe("configured");
        _setting.Saves.ShouldBe([("local-user", "/opt/tools/bun/bin/bun")]);
        (await SwitchOnAsync()).ShouldBeTrue();
        _bun.Ensures.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Clearing_the_path_clears_it_and_leaves_the_switch_alone(string? path)
    {
        _setting.Seed("local-user", "/opt/tools/bun/bin/bun");

        var response = await _client.PutAsJsonAsync($"{Runtime}/bun-path", new { path });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("configuredPath").ValueKind.ShouldBe(JsonValueKind.Null);
        _setting.Saves.ShouldBe([("local-user", null)]);
        (await SwitchOnAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_relative_path_is_a_400_that_says_to_start_at_the_root()
    {
        var response = await _client.PutAsJsonAsync($"{Runtime}/bun-path", new { path = "tools/bun" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .ShouldBe("The path must start at the root, like /opt/tools/bun/bin/bun or C:\\Tools\\bun\\bun.exe.");
    }

    [Fact]
    public async Task An_unknown_member_is_a_400()
    {
        var response = await _client.PutAsJsonAsync($"{Runtime}/bun-path", new { path = "/opt/bun", extra = 1 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition never came true.");
            await Task.Delay(20);
        }
    }

    /// <summary>A Bun installer that installs on cue and answers checks from a table.</summary>
    private sealed class ScriptedBunRuntime : IBunRuntime
    {
        private readonly List<BunLocation> _installed = [];
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _gate;
        private bool _waitForCancel;

        public int Ensures { get; private set; }
        public BunInstallJob? Job { get; private set; }
        public Dictionary<string, BunCandidate> Checks { get; } = [];
        public Dictionary<string, BunLocation> UserBun { get; } = [];
        public IReadOnlyList<BunCandidate> Found { get; set; } = [];
        public Task InstallStarted => _started.Task;

        public void HoldInstall(bool waitForCancel = false)
        {
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waitForCancel = waitForCancel;
        }

        public void Release() => _gate?.TrySetResult();

        public Task<BunLocation?> FindAsync(BunRelease release, CancellationToken ct)
            => Task.FromResult(_installed.FirstOrDefault(b => b.Version == release.Version) ?? _installed.FirstOrDefault());

        public async Task<BunLocation?> FindForUserAsync(BunRelease release, string userId, CancellationToken ct)
            => UserBun.TryGetValue(userId, out var own) ? own : await FindAsync(release, ct);

        public async Task<Result<BunLocation>> EnsureAsync(BunRelease release, IProgress<BunInstallJob>? progress, CancellationToken ct)
        {
            Ensures++;
            void Report(string phase, string? reason = null)
            {
                Job = new BunInstallJob(phase, release.Version, phase, 0, null, reason);
                progress?.Report(Job);
            }

            Report(BunInstallPhases.Downloading);
            _started.TrySetResult();
            if (_gate is not null)
            {
                try
                {
                    if (_waitForCancel)
                        await Task.Delay(Timeout.Infinite, ct);
                    else
                        await _gate.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    Report(BunInstallPhases.Failed, BunInstallFailures.Cancelled);
                    throw;
                }
            }

            var location = new BunLocation($"/bun/{release.Version}/bun", BunSources.Installed, release.Version);
            _installed.Add(location);
            Report(BunInstallPhases.Succeeded);
            return location;
        }

        public Task<IReadOnlyList<BunCandidate>> FindOnMachineAsync(CancellationToken ct) => Task.FromResult(Found);

        public Task<BunCandidate> CheckAsync(string path, CancellationToken ct)
            => Task.FromResult(Checks.GetValueOrDefault(path) ?? new BunCandidate(path, path, null, BunCandidateStatuses.NotWorking, $"There's no file at {path}."));

        public IReadOnlyList<BunLocation> Installed() => _installed;

        public Task<IReadOnlyList<string>> PruneAsync(IReadOnlyCollection<string> inUse, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

        public BunSafety SafetyOf(BunLocation location, BunRelease release) => new(true, false, null);
    }
}

/// <summary>
/// The bun-path route against the real installer's checks, with real scripts standing in for Bun: the messages for a path
/// that is a folder, missing, not working, too old, or changeable by others.
/// </summary>
public sealed class ModsRuntimeBunPathValidationTests : IAsyncDisposable
{
    private const string Url = "/api/features/mods/runtime/bun-path";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"fleet-bunpath-{Guid.NewGuid():N}");
    private readonly ApiWebApplicationFactory _factory = new(authEnabled: false);
    private readonly HttpClient _client;

    public ModsRuntimeBunPathValidationTests()
    {
        Directory.CreateDirectory(_folder);
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Script(string name, string body)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private async Task<(HttpStatusCode Status, string? Error)> PutAsync(string path)
    {
        var response = await _client.PutAsJsonAsync(Url, new { path });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body.TryGetProperty("error", out var error) ? error.GetString() : null);
    }

    [Fact]
    public async Task A_working_bun_is_saved_for_the_user_and_shown_in_the_view()
    {
        if (OperatingSystem.IsWindows()) return;
        var bun = Script("bun", "echo 1.4.7");

        var response = await _client.PutAsJsonAsync(Url, new { path = bun });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        view.GetProperty("configuredPath").GetString().ShouldBe(bun);
        view.GetProperty("bun").GetProperty("source").GetString().ShouldBe("configured");
        view.GetProperty("bun").GetProperty("version").GetString().ShouldBe("1.4.7");
        (await _client.GetFromJsonAsync<JsonElement>("/api/preferences")).GetProperty("ModsBunPath").GetString().ShouldBe(bun);
        (await _client.GetFromJsonAsync<JsonElement>("/api/features/mods")).GetProperty("on").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_folder_is_refused_and_named()
    {
        if (OperatingSystem.IsWindows()) return;

        var (status, error) = await PutAsync(_folder);

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldBe($"{_folder} is a folder, not the bun program.");
    }

    [Fact]
    public async Task A_missing_file_is_refused_and_named()
    {
        if (OperatingSystem.IsWindows()) return;
        var missing = Path.Combine(_folder, "nope");

        var (status, error) = await PutAsync(missing);

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldBe($"There's no file at {missing}.");
    }

    [Fact]
    public async Task A_program_that_does_not_run_as_bun_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        var broken = Script("broken", "exit 3");

        var (status, error) = await PutAsync(broken);

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldNotBeNullOrWhiteSpace();
        (await _client.GetFromJsonAsync<JsonElement>("/api/features/mods")).GetProperty("on").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_bun_older_than_the_oldest_mods_run_on_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        var old = Script("old", "echo 1.3.0");

        var (status, error) = await PutAsync(old);

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldBe("Bun 1.3.0 is older than 1.4.0, the oldest Bun mods run on. Run bun upgrade to update it.");
    }

    [Fact]
    public async Task A_bun_that_others_can_change_is_refused_without_running_it()
    {
        if (OperatingSystem.IsWindows()) return;
        var marker = Path.Combine(_folder, "ran");
        var open = Script("open", $"touch {marker}; echo 1.4.7");
        File.SetUnixFileMode(open, (UnixFileMode)0b111_111_111);

        var (status, error) = await PutAsync(open);

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldContain("can be changed by other users");
        File.Exists(marker).ShouldBeFalse();
    }

    [Fact]
    public async Task A_bun_in_a_folder_that_others_can_change_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        var shared = Path.Combine(_folder, "shared");
        Directory.CreateDirectory(shared);
        var bun = Path.Combine(shared, "bun");
        File.WriteAllText(bun, "#!/bin/sh\necho 1.4.7\n");
        File.SetUnixFileMode(bun, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(shared, (UnixFileMode)0b111_111_111);

        var (status, error) = await PutAsync(bun);

        status.ShouldBe(HttpStatusCode.BadRequest);
        error.ShouldBe($"Fleet didn't run it: {shared} can be changed by other users.");
    }
}
