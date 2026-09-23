using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// Profiles on OpenCode 2: the file a server reads, which server a session runs on, and the check before a profile is
/// saved. The log lines and config documents are V2's own (2.0.9, <c>~/.cache/opencode2-profiles/p3-diagnostics.sh</c>).
/// </summary>
public sealed class OpenCode2ProfilesTests : IDisposable
{
    private const string ProfilePath = "/data/opencode2/profiles/profile-0123456789abcdef.json";

    private readonly string _data = Path.Combine(Path.GetTempPath(), $"fleet-oc2-profiles-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void A_profile_version_is_written_once_to_a_file_named_by_its_content()
    {
        var first = OpenCode2Profiles.Write(_data, """{ "model": "work/big" }""");
        var again = OpenCode2Profiles.Write(_data, """{ "model": "work/big" }""");
        var edited = OpenCode2Profiles.Write(_data, """{ "model": "work/small" }""");

        again.ShouldBe(first);
        edited.Hash.ShouldNotBe(first.Hash);
        first.ConfigPath.ShouldBe(Path.Combine(_data, "opencode2", "profiles", $"profile-{first.Hash}.json"));
        File.ReadAllText(first.ConfigPath).ShouldBe("""{ "model": "work/big" }""");
        File.ReadAllText(edited.ConfigPath).ShouldBe("""{ "model": "work/small" }""");
    }

    [Fact]
    public async Task A_session_on_a_profile_is_prepared_with_its_file_and_one_without_with_none()
    {
        await using var runtime = Runtime();

        var profiled = await runtime.PrepareRuntimeAsync(Context(new HarnessProfile { Name = "Work", Content = """{"model":"work/big"}""" }), CancellationToken.None);
        var plain = await runtime.PrepareRuntimeAsync(Context(profile: null), CancellationToken.None);

        var profile = ((OpenCode2LaunchArtifacts)profiled.ShouldBeOfType<RuntimePreparation.Ready>().Artifacts).Profile.ShouldNotBeNull();
        File.ReadAllText(profile.ConfigPath).ShouldBe("""{"model":"work/big"}""");
        ((OpenCode2LaunchArtifacts)plain.ShouldBeOfType<RuntimePreparation.Ready>().Artifacts).Profile.ShouldBeNull();
    }

    [Fact]
    public void Comments_and_trailing_commas_are_fine()
    {
        OpenCode2Profiles.ReadSettings("""
            {
              // work account
              "model": "work/big",
            }
            """, out var settings).ShouldBeNull();

        using (settings)
            settings!.RootElement.GetProperty("model").GetString().ShouldBe("work/big");
    }

    [Fact]
    public void Broken_JSON_says_where_it_breaks()
    {
        var check = OpenCode2Profiles.ReadSettings("{\n  \"model\": \"work/big\"\n  \"agent\": {}\n}", out var settings).ShouldNotBeNull();

        settings.ShouldBeNull();
        check.Ok.ShouldBeFalse();
        check.Error.ShouldBe("This profile isn't valid JSON.");
        check.Details.ShouldNotBeNull().ShouldHaveSingleItem().ShouldStartWith("Line 3, column 3: ");
    }

    [Fact]
    public void A_profile_is_an_object()
    {
        var check = OpenCode2Profiles.ReadSettings("""["model"]""", out _).ShouldNotBeNull();

        check.Error.ShouldBe("This profile isn't a JSON object.");
    }

    [Fact]
    public void A_config_diagnostic_is_read_from_V2s_log_line()
    {
        var diagnostic = OpenCode2Profiles.ParseLogLine(
            $"""timestamp=2026-09-22T18:44:14.455Z level=WARN run=3d7ac5cc message="configuration normalization diagnostic" source={ProfilePath} path=$.agent.bad kind=invalid action="skipped malformed recognized value" http.span=37 role=server""")
            .ShouldNotBeNull();

        diagnostic.Message.ShouldBe("configuration normalization diagnostic");
        diagnostic.Fields["source"].ShouldBe(ProfilePath);
        diagnostic.Fields["path"].ShouldBe("$.agent.bad");
        diagnostic.Fields["kind"].ShouldBe("invalid");
        diagnostic.Fields["action"].ShouldBe("skipped malformed recognized value");
    }

    [Fact]
    public void A_plugin_that_failed_is_read_from_V2s_log_line()
    {
        var diagnostic = OpenCode2Profiles.ParseLogLine(
            """timestamp=2026-09-22T18:42:55.042Z level=WARN run=37d3ec8c message="failed to load plugin" target=/does/not/exist ref=err_2229c233 cause="Cause([Die(Error: ENOENT: no such file or directory, stat '/does/not/exist')])" http.span=165 role=server""")
            .ShouldNotBeNull();

        diagnostic.Message.ShouldBe("failed to load plugin");
        diagnostic.Fields["target"].ShouldBe("/does/not/exist");
        diagnostic.Fields["cause"].ShouldBe("Cause([Die(Error: ENOENT: no such file or directory, stat '/does/not/exist')])");
    }

    [Theory]
    [InlineData("""timestamp=2026-09-22T18:42:21Z level=INFO message="skills rescanned" skills="[]" """)]
    [InlineData("server listening on http://127.0.0.1:4981")]
    [InlineData("")]
    public void Other_log_lines_are_not_diagnostics(string line)
        => OpenCode2Profiles.ParseLogLine(line).ShouldBeNull();

    [Fact]
    public void A_profile_V2_uses_in_full_passes()
    {
        using var settings = JsonDocument.Parse("""{"$schema":"https://opencode.ai/config.json","model":"work/big","provider":{"work":{}},"agent":{"good":{}}}""");

        var check = OpenCode2Profiles.Judge(ProfilePath, settings.RootElement,
            Config("""{"$schema":"https://opencode.ai/config.json","model":{"providerID":"work","model":"big"},"providers":{"work":{}},"agents":{"good":{}}}"""),
            Model("work", "big"), []);

        check.ShouldBe(HarnessProfileCheck.Passed);
    }

    [Fact]
    public void Values_V2_skipped_and_settings_it_ignores_are_named()
    {
        using var settings = JsonDocument.Parse("""{"model":5,"logLevel":"DEBUG","agent":{"bad":{"mode":"bogus"}}}""");
        string[] log =
        [
            Diagnostic("$.agent.bad", "invalid", "skipped malformed recognized value"),
            Diagnostic("$.logLevel", "unsupported", "omitted unsupported legacy setting"),
            Diagnostic("$.model", "invalid", "skipped malformed recognized value"),
            // The same document is read once per folder V2 loads.
            Diagnostic("$.model", "invalid", "skipped malformed recognized value"),
            Diagnostic("$.model", "invalid", "skipped malformed recognized value", source: "/home/you/.config/opencode/opencode.json"),
        ];

        var check = OpenCode2Profiles.Judge(ProfilePath, settings.RootElement, Config("{}"), Model("opencode", "free"), Parse(log));

        check.Ok.ShouldBeFalse();
        check.Error.ShouldBe("OpenCode 2 would leave out part of this profile.");
        check.Details.ShouldBe(
        [
            "agent.bad: OpenCode 2 skipped this value because it isn't valid.",
            "logLevel: OpenCode 2 doesn't support this setting and ignores it.",
            "model: OpenCode 2 skipped this value because it isn't valid.",
        ]);
    }

    [Fact]
    public void A_setting_V2_does_not_know_is_named_since_V2_drops_it_without_a_word()
    {
        using var settings = JsonDocument.Parse("""{"nonsense":true,"provider":{"work":{}},"brand_new":1}""");

        // V2 stored what it knows, including a setting newer than Fleet's list; "nonsense" left no trace.
        var check = OpenCode2Profiles.Judge(ProfilePath, settings.RootElement,
            Config("""{"providers":{"work":{}},"brand_new":1}"""), null, []);

        check.Details.ShouldBe(["nonsense: OpenCode 2 doesn't know this setting and ignores it."]);
    }

    [Fact]
    public void A_profile_V2_did_not_read_at_all_fails()
    {
        using var settings = JsonDocument.Parse("{}");

        var check = OpenCode2Profiles.Judge(ProfilePath, settings.RootElement, [],
            null, Parse([Diagnostic("$", "invalid", "rejected malformed JSON or JSONC document")]));

        check.Error.ShouldBe("OpenCode 2 couldn't read this profile.");
        check.Details.ShouldBe(["The profile: OpenCode 2 couldn't read it as JSON."]);
    }

    [Fact]
    public void A_plugin_of_the_profile_that_did_not_load_is_named_with_V2s_reason()
    {
        using var settings = JsonDocument.Parse("""{"plugin":["/does/not/exist","file:///opt/fine"]}""");
        var log = Parse(
        [
            """level=WARN message="failed to load plugin" target=/does/not/exist cause="Cause([Die(Error: ENOENT: no such file or directory, stat '/does/not/exist')])" """,
            // Someone else's plugin: the user's own config or Fleet's.
            """level=WARN message="failed to load plugin" target=/elsewhere cause="Cause([Die(Error: nope)])" """,
        ]);

        var check = OpenCode2Profiles.Judge(ProfilePath, settings.RootElement, Config("""{"plugins":["/does/not/exist","file:///opt/fine"]}"""), null, log);

        check.Details.ShouldBe(["plugin /does/not/exist: OpenCode 2 couldn't load it (ENOENT: no such file or directory, stat '/does/not/exist')."]);
    }

    [Fact]
    public void A_model_V2_has_no_provider_for_is_named_with_the_one_sessions_would_get()
    {
        // V2 keeps the model, finds no provider for it, and falls back to another one without saying so.
        using var settings = JsonDocument.Parse("""{"model":"nope/nope"}""");

        var check = OpenCode2Profiles.Judge(ProfilePath, settings.RootElement,
            Config("""{"model":{"providerID":"nope","model":"nope"}}"""), Model("opencode", "mimo-v2.6-flash-free"), []);

        check.Details.ShouldBe(["model: OpenCode 2 has no model nope/nope, so sessions would use opencode/mimo-v2.6-flash-free instead. Check the provider is set up."]);
    }

    [Fact]
    public async Task Each_profile_version_gets_a_server_of_its_own_and_no_profile_keeps_the_owners()
    {
        var started = new List<OpenCode2ServerKey>();
        await using var servers = Servers(started);
        var work = new OpenCode2Profile("aaaa", "/p/a.json");
        var edited = new OpenCode2Profile("bbbb", "/p/b.json");

        var plain = await servers.GetAsync(OpenCode2ServerKey.For("local-user", null), Setup(null), CancellationToken.None);
        var onWork = await servers.GetAsync(OpenCode2ServerKey.For("local-user", work), Setup(work), CancellationToken.None);
        var onWorkAgain = await servers.GetAsync(OpenCode2ServerKey.For("local-user", work), Setup(work), CancellationToken.None);
        var onEdited = await servers.GetAsync(OpenCode2ServerKey.For("local-user", edited), Setup(edited), CancellationToken.None);

        onWorkAgain.ShouldBeSameAs(onWork);
        new[] { plain, onWork, onEdited }.Distinct().Count().ShouldBe(3);
        onWork.Profile.ShouldBe(work);
        plain.Profile.ShouldBeNull();
        started.ShouldBe([new("local-user", null), new("local-user", "aaaa"), new("local-user", "bbbb")]);
    }

    [Fact]
    public async Task A_profile_server_nobody_used_for_the_idle_time_stops_and_the_owners_own_stays()
    {
        await using var servers = Servers([]);
        var work = new OpenCode2Profile("aaaa", "/p/a.json");
        var plain = await servers.GetAsync(OpenCode2ServerKey.For("local-user", null), Setup(null), CancellationToken.None);
        var onWork = await servers.GetAsync(OpenCode2ServerKey.For("local-user", work), Setup(work), CancellationToken.None);

        (await servers.StopIdleAsync(DateTimeOffset.UtcNow.AddMinutes(4), CancellationToken.None)).ShouldBe(0);
        onWork.IsRunning.ShouldBeTrue();

        (await servers.StopIdleAsync(DateTimeOffset.UtcNow.AddMinutes(6), CancellationToken.None)).ShouldBe(1);
        onWork.IsRunning.ShouldBeFalse();
        plain.IsRunning.ShouldBeTrue();
        servers.All.ShouldBe([plain]);

        // The next session on the profile starts it again.
        var again = await servers.GetAsync(OpenCode2ServerKey.For("local-user", work), Setup(work), CancellationToken.None);
        again.ShouldNotBeSameAs(onWork);
        again.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task A_profile_server_with_a_turn_running_does_not_stop()
    {
        await using var servers = Servers([], active: """{"data":{"ses_a":{"type":"running"}}}""");
        var work = new OpenCode2Profile("aaaa", "/p/a.json");
        var onWork = await servers.GetAsync(OpenCode2ServerKey.For("local-user", work), Setup(work), CancellationToken.None);

        (await servers.StopIdleAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None)).ShouldBe(0);
        onWork.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task A_delegated_child_finds_the_server_its_parent_listens_on()
    {
        await using var servers = Servers([]);
        var work = new OpenCode2Profile("aaaa", "/p/a.json");
        _ = await servers.GetAsync(OpenCode2ServerKey.For("local-user", null), Setup(null), CancellationToken.None);
        var onWork = await servers.GetAsync(OpenCode2ServerKey.For("local-user", work), Setup(work), CancellationToken.None);
        onWork.Attach("ses_parent", new Sink("fleet-parent"));

        servers.FindServing("local-user", "fleet-parent").ShouldBeSameAs(onWork);
        servers.FindServing("someone-else", "fleet-parent").ShouldBeNull();
        servers.FindServing("local-user", "fleet-other").ShouldBeNull();
    }

    [Fact]
    public async Task A_profile_server_with_a_background_shell_running_stops_only_once_the_shell_has_finished()
    {
        // The turn that backgrounded the shell ended long ago; V2 lists the shell as running until it exits.
        var shells = new Dictionary<string, string[]> { ["/work"] = [OpenCode2Fixtures.Shell("running")] };
        await using var servers = Servers([], shells: shells);
        var work = new OpenCode2Profile("aaaa", "/p/a.json");
        var onWork = await servers.GetAsync(OpenCode2ServerKey.For("local-user", work), Setup(work), CancellationToken.None);

        (await servers.StopIdleAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None)).ShouldBe(0);
        onWork.IsRunning.ShouldBeTrue();

        shells["/work"] = [];
        (await servers.StopIdleAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None)).ShouldBe(1);
        onWork.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task A_server_started_with_other_settings_is_replaced_only_once_its_background_shell_has_finished()
    {
        var shells = new Dictionary<string, string[]> { ["/work"] = [OpenCode2Fixtures.Shell("running")] };
        await using var servers = Servers([], shells: shells);
        var key = OpenCode2ServerKey.For("local-user", null);
        var before = await servers.GetAsync(key, OpenCode2ServerSetup.None, CancellationToken.None);
        var changed = OpenCode2ServerSetup.None with { SessionMessages = true };

        (await servers.GetAsync(key, changed, CancellationToken.None)).ShouldBeSameAs(before);

        shells["/work"] = [];
        var after = await servers.GetAsync(key, changed, CancellationToken.None);
        after.ShouldNotBeSameAs(before);
        after.Setup.ShouldBe(changed);
        before.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task After_an_update_a_server_with_a_background_shell_running_is_kept_until_it_has_finished()
    {
        var shells = new Dictionary<string, string[]> { ["/work"] = [OpenCode2Fixtures.Shell("running")] };
        await using var servers = Servers([], shells: shells);
        var key = OpenCode2ServerKey.For("local-user", null);
        var before = await servers.GetAsync(key, OpenCode2ServerSetup.None, CancellationToken.None);

        await servers.AfterUpdateAsync(CancellationToken.None);

        before.IsRunning.ShouldBeTrue();
        before.IsOutdated.ShouldBeTrue();
        (await servers.GetAsync(key, OpenCode2ServerSetup.None, CancellationToken.None)).ShouldBeSameAs(before);

        shells["/work"] = [];
        (await servers.GetAsync(key, OpenCode2ServerSetup.None, CancellationToken.None)).ShouldNotBeSameAs(before);
    }

    private static OpenCode2Servers Servers(
        List<OpenCode2ServerKey> started,
        string active = """{"data":{}}""",
        IReadOnlyDictionary<string, string[]>? shells = null)
        => new(
            (key, setup, _) =>
            {
                started.Add(key);
                var api = OpenCode2Fixtures.Running(active, shells);
                return Task.FromResult(new OpenCode2Server(key.OwnerUserId, OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance, setup));
            },
            TimeSpan.FromMinutes(5),
            NullLogger.Instance);

    private static OpenCode2ServerSetup Setup(OpenCode2Profile? profile) => OpenCode2ServerSetup.None with { Profile = profile };

    private OpenCode2HarnessRuntime Runtime()
    {
        var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        return new OpenCode2HarnessRuntime(
            services.GetRequiredService<IHttpClientFactory>(),
            new FleetOptions { DatabasePath = Path.Combine(_data, "fleet.db") },
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OpenCode2HarnessRuntime>.Instance,
            NullLoggerFactory.Instance);
    }

    private static RuntimePreparationContext Context(HarnessProfile? profile) => new()
    {
        UserId = "local-user",
        UserCredentials = [],
        WorkingDirectory = "/work",
        Profile = profile,
    };

    private static List<OpenCode2ConfigSource> Config(string profileInfo)
        => JsonSerializer.Deserialize($$$$"""
            [
              {"type":"document","path":"/home/you/.config/opencode/opencode.json","info":{"model":{"providerID":"base","model":"base-model"}}},
              {"type":"directory","path":"/home/you/.config/opencode"},
              {"type":"document","path":"{{{{ProfilePath}}}}","info":{{{{profileInfo}}}}},
              {"type":"document","info":{"plugins":["/data/opencode2/fleet"]}}
            ]
            """, OpenCode2JsonContext.Default.ListOpenCode2ConfigSource)!;

    private static OpenCode2ModelInfo Model(string provider, string id) => new() { ProviderId = provider, Id = id };

    private static string Diagnostic(string path, string kind, string action, string source = ProfilePath)
        => $"""timestamp=2026-09-22T18:44:14Z level=WARN run=r message="configuration normalization diagnostic" source={source} path={path} kind={kind} action="{action}" http.span=1 role=server""";

    private static List<OpenCode2Profiles.LogDiagnostic> Parse(IEnumerable<string> lines)
        => lines.Select(OpenCode2Profiles.ParseLogLine).OfType<OpenCode2Profiles.LogDiagnostic>().ToList();

    private sealed class Sink(string fleetSessionId) : IOpenCode2EventSink
    {
        public OpenCode2SessionContext Context { get; } = new(fleetSessionId, "local-user", "/work", null, null);

        public void OnEvent(OpenCode2Event evt)
        {
        }

        public Task ResyncAsync(IReadOnlySet<string> activeSessions, CancellationToken ct) => Task.CompletedTask;

        public void OnServerStopped()
        {
        }
    }
}
