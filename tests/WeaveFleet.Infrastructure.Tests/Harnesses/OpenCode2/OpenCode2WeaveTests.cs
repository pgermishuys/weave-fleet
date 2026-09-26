using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Weave;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Weave;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

public sealed class OpenCode2WeaveTests : IAsyncLifetime
{
    private const string Owner = "local-user";
    private const string Entry = "@weaveio/weave-adapter-opencode2@0.2.0-next.3";

    // Lines from a real OpenCode 2.0.16 log: a Weave version npm doesn't have, and OpenCode 1's Weave adapter, which a
    // config shared with OpenCode 1 lists under "plugin" and V2 tries too.
    private const string MissingVersionLine = """timestamp=2026-09-26T07:53:51.871Z level=WARN run=feb9c753 message="failed to load plugin" target=@weaveio/weave-adapter-opencode2@9.9.9-nope ref=err_6a09dd44 cause="Cause([Fail(NpmInstallFailedError (cause: @weaveio/weave-adapter-opencode2: No matching version found for @weaveio/weave-adapter-opencode2@9.9.9-nope.))])" http.span=4840 role=server""";
    private const string V1AdapterLine = """timestamp=2026-09-26T07:53:51.856Z level=WARN run=feb9c753 message="failed to load plugin" target=@weaveio/weave-adapter-opencode@0.2.0-next.1 ref=err_9697c2b2 cause="Cause([Fail(PluginModule.LoadError: Plugin must export a default definition with an id and an effect or setup function. (cause: SchemaError(Expected object\n  at [\"default\"])))])" http.span=4825 role=server""";

    private readonly string _data = Path.Combine(Path.GetTempPath(), $"opencode2-weave-{Guid.NewGuid():N}");
    private readonly InMemoryWeaveConfigRepository _configs = new();
    private readonly List<OpenCode2Server> _servers = [];
    private readonly List<(string Folder, Dictionary<string, string> Files)> _trials = [];
    private Func<StubHandler> _trialApi = () => TrialApi([], []);
    private string[] _trialLog = [];
    private OpenCode2Weave _weave = null!;

    public Task InitializeAsync()
    {
        var services = new ServiceCollection().AddSingleton<IWeaveConfigRepository>(_configs).BuildServiceProvider();
        _weave = new OpenCode2Weave(
            () => _data,
            services.GetRequiredService<IServiceScopeFactory>(),
            (_, folder, logLine, _) =>
            {
                _trials.Add((folder, Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .ToDictionary(file => Path.GetRelativePath(folder, file), File.ReadAllText)));
                foreach (var line in _trialLog)
                    logLine(line);
                return Task.FromResult(NewServer(_trialApi()));
            },
            owner => _servers.Where(server => server.OwnerUserId == owner).ToList(),
            NullLogger.Instance)
        {
            AgentWait = TimeSpan.FromMilliseconds(300),
            LogGrace = TimeSpan.Zero,
        };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var server in _servers)
            await server.DisposeAsync();
        if (Directory.Exists(_data))
            Directory.Delete(_data, recursive: true);
    }

    private static OpenCode2Server NewServer(StubHandler api, OpenCode2ServerSetup? setup = null)
        => new(Owner, OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance, setup)
        {
            // The stub sends no catalog events, so the folder never reports loaded.
            LocationLoadTimeout = TimeSpan.FromMilliseconds(50),
        };

    /// <summary>
    /// A trial server that lists <paramref name="plugins"/> and <paramref name="agents"/> in any folder, and whose config
    /// lists <paramref name="listed"/>.
    /// </summary>
    private static StubHandler TrialApi(object[] plugins, object[] agents, string[]? listed = null)
        => new(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/plugin" => Json(new { data = plugins }),
            "/api/agent" => Json(new { data = agents }),
            "/api/config" => Json(new[] { new { type = "document", path = "/home/you/.config/opencode/opencode.json", info = new { plugins = listed ?? [] } } }),
            _ => Json(new { }),
        });

    private static object Builtin(string id) => new { id, source = new { type = "builtin" } };

    private static object Weave(string target = Entry) => new { id = "weave", source = new { type = "package", target, version = "0.2.0-next.3" } };

    private static object Agent(string id, string? description = null) => new { id, name = id, description };

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task detection_finds_the_adapter_and_that_it_reads_fleets_folder()
    {
        _trialApi = () => TrialApi(
            [Builtin("opencode.agent"), Weave()],
            [Agent("build", "The default agent."), Agent("loom", "[weave-managed] Main orchestrator"), Agent(OpenCode2Weave.ProbeAgent, "[weave-managed] Fleet checks")]);
        _trialLog = [V1AdapterLine];

        var installs = await _weave.DetectAsync(Owner, CancellationToken.None);

        installs.ShouldBe([new WeaveInstall(WeaveFlavor.Weave, OpenCode2Weave.Package, Entry, true)]);
        var trial = _trials.ShouldHaveSingleItem();
        trial.Folder.ShouldBe(WeaveConfigFolder.TrialFor(_data, Owner, "opencode2"));
        trial.Files["config.weave"].ShouldContain($"agent {OpenCode2Weave.ProbeAgent}");
    }

    [Fact]
    public async Task detection_waits_for_a_listed_adapter_that_v2_is_still_installing()
    {
        var reads = 0;
        _trialApi = () => new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            // npm is still fetching the package for the first two reads.
            "/api/plugin" => Json(new { data = ++reads > 2 ? new[] { Weave() } : [] }),
            "/api/agent" => Json(new { data = new[] { Agent("loom", "[weave-managed] Main orchestrator") } }),
            "/api/config" => Json(new[] { new { type = "document", info = new { plugins = new[] { Entry } } } }),
            _ => Json(new { }),
        });

        var install = (await _weave.DetectAsync(Owner, CancellationToken.None)).ShouldHaveSingleItem();

        install.Entry.ShouldBe(Entry);
        reads.ShouldBe(3);
    }

    [Fact]
    public void the_plugins_a_config_lists_are_read_as_packages_or_descriptors()
        => OpenCode2Weave.ListedPlugins(
        [
            new OpenCode2ConfigSource { Info = JsonDocument.Parse("""{ "plugins": ["a@1", { "package": "b@2", "options": {} }] }""").RootElement },
            new OpenCode2ConfigSource { Info = JsonDocument.Parse("""{ "model": "x" }""").RootElement },
        ]).ShouldBe(["a@1", "b@2"]);

    [Fact]
    public async Task an_adapter_that_doesnt_read_fleets_folder_is_found_but_cant_take_a_config()
    {
        _trialApi = () => TrialApi([Weave("@weaveio/weave-adapter-opencode2@0.1.0")], [Agent("loom", "[weave-managed] Main orchestrator")]);

        var install = (await _weave.DetectAsync(Owner, CancellationToken.None)).ShouldHaveSingleItem();

        install.Entry.ShouldBe("@weaveio/weave-adapter-opencode2@0.1.0");
        install.AcceptsFleetConfig.ShouldBeFalse();
    }

    [Fact]
    public async Task an_adapter_v2_couldnt_load_is_found_with_the_reason()
    {
        _trialLog = [V1AdapterLine, MissingVersionLine];

        var install = (await _weave.DetectAsync(Owner, CancellationToken.None)).ShouldHaveSingleItem();

        install.Entry.ShouldBe("@weaveio/weave-adapter-opencode2@9.9.9-nope");
        install.AcceptsFleetConfig.ShouldBeFalse();
        install.Error.ShouldBe("NpmInstallFailedError (cause: @weaveio/weave-adapter-opencode2: No matching version found for @weaveio/weave-adapter-opencode2@9.9.9-nope.)");
    }

    [Fact]
    public async Task no_weave_in_the_plugin_list_is_no_install()
    {
        _trialApi = () => TrialApi([Builtin("opencode.agent")], [Agent("build", "The default agent.")]);
        _trialLog = [V1AdapterLine];

        (await _weave.DetectAsync(Owner, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public void a_local_build_is_named_by_its_package_json()
    {
        var build = Path.Combine(_data, "weave", "packages", "adapters", "opencode2");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "package.json"), """{ "name": "@weaveio/weave-adapter-opencode2" }""");

        var described = OpenCode2Weave.Describe(new OpenCode2PluginInfo
        {
            Id = "weave",
            Source = new OpenCode2PluginSource { Type = "local", Path = Path.Combine(build, "server.js") },
        });

        described.ShouldBe((OpenCode2Weave.Package, new Uri(build).AbsoluteUri));
    }

    [Fact]
    public void v2s_own_plugins_are_not_described()
        => OpenCode2Weave.Describe(new OpenCode2PluginInfo { Id = "opencode.agent", Source = new OpenCode2PluginSource { Type = "builtin" } }).ShouldBeNull();

    [Fact]
    public void each_failed_plugin_is_read_from_the_log_without_v2s_wrapping()
        => OpenCode2Weave.FailedPlugins(["timestamp=… level=INFO msg=\"loading plugin\" id=x", V1AdapterLine, MissingVersionLine]).ShouldBe(
        [
            ("@weaveio/weave-adapter-opencode@0.2.0-next.1",
                "PluginModule.LoadError: Plugin must export a default definition with an id and an effect or setup function. (cause: SchemaError(Expected object at [\"default\"]))"),
            ("@weaveio/weave-adapter-opencode2@9.9.9-nope",
                "NpmInstallFailedError (cause: @weaveio/weave-adapter-opencode2: No matching version found for @weaveio/weave-adapter-opencode2@9.9.9-nope.)"),
        ]);

    [Fact]
    public async Task a_draft_lists_the_agents_weave_added()
    {
        _trialApi = () => TrialApi([Weave()], [Agent("build", "The default agent."), Agent("loom", "[weave-managed] Main orchestrator"), Agent("reviewer", "[weave-managed] Reviews")]);
        var files = new Dictionary<string, string> { ["config.weave"] = "agent reviewer {}" };

        var check = await _weave.CheckAsync(Owner, WeaveFlavor.Weave, files, CancellationToken.None);

        check.ShouldBe(new WeaveCheck(true, ["loom", "reviewer"]), new WeaveCheckComparer());
        _trials.ShouldHaveSingleItem().Files["config.weave"].ShouldBe("agent reviewer {}");
    }

    [Fact]
    public async Task a_draft_weave_cant_read_leaves_no_weave_agents()
    {
        _trialApi = () => TrialApi([Weave()], [Agent("build", "The default agent.")]);

        var check = await _weave.CheckAsync(Owner, WeaveFlavor.Weave, new Dictionary<string, string> { ["config.weave"] = "agent broken {" }, CancellationToken.None);

        check.ShouldNotBeNull().Ok.ShouldBeFalse();
        check.Error.ShouldBe("OpenCode 2 started, but Weave added no agents. Weave ignores the whole file when it can't read it.");
    }

    [Fact]
    public async Task weave_legacy_is_not_opencode_2s_to_try()
        => (await _weave.CheckAsync(Owner, WeaveFlavor.Legacy, new Dictionary<string, string>(), CancellationToken.None)).ShouldBeNull();

    [Fact]
    public async Task a_config_kept_in_fleet_is_written_to_the_folder_servers_read()
    {
        (await _weave.GetConfigFolderAsync(Owner)).ShouldBeNull();

        await _configs.SaveAsync(new WeaveConfig
        {
            Source = WeaveConfigSource.Fleet,
            Files = new Dictionary<string, string> { ["config.weave"] = "agent a {}", ["prompts/team.md"] = "Be brief." },
        });
        var folder = await _weave.GetConfigFolderAsync(Owner);

        folder.ShouldBe(WeaveConfigFolder.ForUser(_data, Owner));
        File.ReadAllText(Path.Combine(folder!, "config.weave")).ShouldBe("agent a {}");
        File.ReadAllText(Path.Combine(folder!, "prompts", "team.md")).ShouldBe("Be brief.");
    }

    [Fact]
    public async Task a_config_with_only_legacys_file_is_not_opencode_2s()
    {
        await _configs.SaveAsync(new WeaveConfig
        {
            Source = WeaveConfigSource.Fleet,
            Files = new Dictionary<string, string> { ["weave-opencode.jsonc"] = "{}" },
        });

        (await _weave.GetConfigFolderAsync(Owner)).ShouldBeNull();
    }

    [Fact]
    public async Task a_plugin_change_reloads_each_server_unless_a_question_is_open()
    {
        var quiet = RunningApi(active: [], forms: []);
        var asking = RunningApi(active: ["ses_q"], forms: ["frm_1"]);
        _servers.Add(NewServer(quiet));
        _servers.Add(NewServer(asking));

        await _weave.PluginsChangedAsync(Owner, CancellationToken.None);

        quiet.Requests.ShouldContain(r => r.Method == HttpMethod.Post && r.Path == "/api/location/reload");
        asking.Requests.ShouldNotContain(r => r.Path == "/api/location/reload");
        asking.Requests.ShouldContain(r => r.Path == "/api/session/ses_q/form");
    }

    [Fact]
    public async Task a_saved_config_reloads_only_the_servers_that_read_fleets_folder()
    {
        await _configs.SaveAsync(new WeaveConfig
        {
            Source = WeaveConfigSource.Fleet,
            Files = new Dictionary<string, string> { ["config.weave"] = "agent a {}" },
        });
        var reads = RunningApi(active: [], forms: []);
        var own = RunningApi(active: [], forms: []);
        _servers.Add(NewServer(reads, OpenCode2ServerSetup.None with { WeaveConfigFolder = WeaveConfigFolder.ForUser(_data, Owner) }));
        _servers.Add(NewServer(own));

        await _weave.ConfigChangedAsync(Owner, CancellationToken.None);

        reads.Requests.ShouldContain(r => r.Path == "/api/location/reload");
        own.Requests.ShouldNotContain(r => r.Path == "/api/location/reload");
        _weave.GetApplyStatus(Owner).ShouldNotBeNull().Error.ShouldBeNull();
    }

    /// <summary>A running server's answers: <paramref name="active"/> sessions, each with <paramref name="forms"/> open.</summary>
    private static StubHandler RunningApi(string[] active, string[] forms)
        => new(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/session/active" => Json(new { data = active.ToDictionary(id => id, _ => new { type = "running" }) }),
            var path when path.EndsWith("/form", StringComparison.Ordinal) => Json(new { data = forms.Select(id => new { id }) }),
            "/api/location/reload" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

    private sealed class WeaveCheckComparer : IEqualityComparer<WeaveCheck>
    {
        public bool Equals(WeaveCheck? x, WeaveCheck? y)
            => x is not null && y is not null && x.Ok == y.Ok && x.Agents.SequenceEqual(y.Agents) && x.Error == y.Error;

        public int GetHashCode(WeaveCheck obj) => obj.Ok.GetHashCode();
    }
}

public sealed class NpmWeavePackageVersionsTests
{
    [Theory]
    [InlineData("""{"latest":"0.1.0","next":"0.2.0-next.3"}""", "0.2.0-next.3")]
    [InlineData("""{"latest":"0.2.0","next":"0.2.0-next.3"}""", "0.2.0")]
    [InlineData("""{"latest":"0.1.1"}""", "0.1.1")]
    [InlineData("""{}""", null)]
    public void the_newer_of_latest_and_next_is_written(string tags, string? expected)
    {
        using var document = JsonDocument.Parse(tags);
        NpmWeavePackageVersions.NewestTagged(document.RootElement).ShouldBe(expected);
    }
}
