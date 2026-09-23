using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Weave;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using WeaveFleet.Infrastructure.Weave;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class OpenCodeWeaveTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string Owner = "user-1";

    private readonly string _data = Path.Combine(Path.GetTempPath(), $"opencode-weave-{Guid.NewGuid():N}");
    private readonly FakeOpenCode _openCode = new();
    private readonly InMemoryWeaveConfigRepository _configs = new();
    private readonly List<InstanceLease> _leases = [];
    private PooledOpenCodeInstanceRegistry _registry = null!;
    private OpenCodeWeave _weave = null!;

    public Task InitializeAsync()
    {
        var spawned = 0;
        _registry = new PooledOpenCodeInstanceRegistry(
            (key, _, _, _) =>
            {
                var httpClient = new OpenCodeHttpClient(
                    new HttpClient(_openCode) { BaseAddress = new Uri("http://127.0.0.1:4096") },
                    NullLogger<OpenCodeHttpClient>.Instance);
                return Task.FromResult(new PooledOpenCodeInstance(
                    key, $"instance-{Interlocked.Increment(ref spawned)}", null, httpClient, null, () => ValueTask.CompletedTask));
            },
            TimeSpan.FromMinutes(1),
            NullLogger<PooledOpenCodeInstanceRegistry>.Instance);

        var services = new ServiceCollection().AddSingleton<IWeaveConfigRepository>(_configs).BuildServiceProvider();
        _weave = new OpenCodeWeave(_registry, () => _data, services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var lease in _leases)
            await lease.DisposeAsync();
        await _registry.DisposeAsync();
        _openCode.Dispose();
        if (Directory.Exists(_data))
            Directory.Delete(_data, recursive: true);
    }

    private string UserFolder => WeaveConfigFolder.ForUser(_data, Owner);

    private async Task LeaseAsync(IReadOnlyDictionary<string, string> environment, string directory)
    {
        _leases.Add(await _registry.AcquireAsync(Owner, CredentialHasher.HashEnvironment(environment), environment, directory, CancellationToken.None));
    }

    private static OpenCodeAgentInfo Agent(string name, string description = "", bool native = false) =>
        new() { Name = name, Description = description, Native = native };

    [Fact]
    public async Task a_fleet_config_points_each_weave_whose_file_it_has_at_the_users_folder()
    {
        await _configs.SaveAsync(new WeaveConfig
        {
            Source = WeaveConfigSource.Fleet,
            Files = new Dictionary<string, string> { ["config.weave"] = "agent a {}", ["prompts/team.md"] = "Be brief." },
        });

        var environment = await _weave.GetEnvironmentAsync(Owner);

        environment.ShouldBe(new Dictionary<string, string> { [WeaveEnvironment.GlobalConfigDir] = UserFolder });
        File.ReadAllText(Path.Combine(UserFolder, "prompts", "team.md")).ShouldBe("Be brief.");
    }

    [Fact]
    public async Task the_users_own_files_mean_fleet_sets_nothing()
    {
        await _configs.SaveAsync(new WeaveConfig
        {
            Source = WeaveConfigSource.Own,
            Files = new Dictionary<string, string> { ["config.weave"] = "agent a {}" },
        });

        (await _weave.GetEnvironmentAsync(Owner)).ShouldBeEmpty();
        Directory.Exists(UserFolder).ShouldBeFalse();
    }

    [Fact]
    public async Task detection_names_each_weave_and_whether_its_probe_agent_showed_up()
    {
        var localBuild = Path.Combine(_data, "legacy-build");
        Directory.CreateDirectory(Path.Combine(localBuild, "dist"));
        File.WriteAllText(Path.Combine(localBuild, "package.json"), """{ "name": "@opencode_weave/weave", "version": "0.8.2" }""");
        _openCode.Plugins = ["@weaveio/weave-adapter-opencode@0.2.0-next.1", $"file://{localBuild}/dist/index.js", "opencode-other-plugin"];
        _openCode.Agents = _ => [Agent("build", native: true), Agent(OpenCodeWeave.WeaveProbeAgent, "probe [weave-managed]")];

        var installs = await _weave.DetectAsync(Owner, CancellationToken.None);

        installs.ShouldBe([
            new WeaveInstall(WeaveFlavor.Weave, "@weaveio/weave-adapter-opencode", "@weaveio/weave-adapter-opencode@0.2.0-next.1", true),
            new WeaveInstall(WeaveFlavor.Legacy, "@opencode_weave/weave", $"file://{localBuild}/dist/index.js", false),
        ]);
    }

    [Fact]
    public async Task a_draft_that_loads_lists_the_weave_agents_it_added()
    {
        _openCode.Agents = _ => [Agent("build", native: true), Agent("loom", "Orchestrates [weave-managed]"), Agent("reviewer", "Reviews [weave-managed]")];

        var check = await _weave.CheckAsync(Owner, WeaveFlavor.Weave, new Dictionary<string, string> { ["config.weave"] = "agent reviewer {}" }, CancellationToken.None);

        check.Ok.ShouldBeTrue();
        check.Agents.ShouldBe(["loom", "reviewer"]);
        File.ReadAllText(Path.Combine(WeaveConfigFolder.TrialFor(_data, Owner), "config.weave")).ShouldBe("agent reviewer {}");
    }

    [Fact]
    public async Task a_draft_weave_cant_read_says_where_from_weaves_log()
    {
        _openCode.Agents = directory =>
        {
            Directory.CreateDirectory(Path.Combine(directory, ".weave"));
            File.WriteAllText(Path.Combine(directory, ".weave", "weave.log"),
                """{"level":50,"errors":[{"type":"ParseError","path":"/x/config.weave","errors":[{"type":"UnclosedBlock","line":2,"column":1}]}],"msg":"Failed to load Weave config — no agents will be materialized"}""" + "\n");
            return [Agent("build", native: true)];
        };

        var check = await _weave.CheckAsync(Owner, WeaveFlavor.Weave, new Dictionary<string, string> { ["config.weave"] = "agent a {" }, CancellationToken.None);

        check.Ok.ShouldBeFalse();
        check.Details.ShouldBe(["config.weave:2:1 UnclosedBlock"]);
    }

    [Fact]
    public async Task a_try_reads_weaves_log_while_weave_keeps_it_open_and_reports_only_its_own_errors()
    {
        // Weave's log stays open in the trial process between tries, as it does on Windows where it can't be deleted.
        FileStream? log = null;
        var tries = 0;
        _openCode.Agents = directory =>
        {
            Directory.CreateDirectory(Path.Combine(directory, ".weave"));
            log ??= new FileStream(Path.Combine(directory, ".weave", "weave.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var line = ++tries == 1
                ? """{"level":50,"errors":[{"type":"ParseError","path":"/x/config.weave","errors":[{"type":"UnclosedBlock","line":2,"column":1}]}],"msg":"Failed to load Weave config"}"""
                : """{"level":30,"msg":"Config loaded successfully"}""";
            log.Write(System.Text.Encoding.UTF8.GetBytes(line + "\n"));
            log.Flush();
            return [Agent("build", native: true)];
        };

        try
        {
            var first = await _weave.CheckAsync(Owner, WeaveFlavor.Weave, new Dictionary<string, string> { ["config.weave"] = "agent a {" }, CancellationToken.None);
            var second = await _weave.CheckAsync(Owner, WeaveFlavor.Weave, new Dictionary<string, string> { ["config.weave"] = "" }, CancellationToken.None);

            first.Details.ShouldBe(["config.weave:2:1 UnclosedBlock"]);
            second.Details.ShouldBeEmpty();
        }
        finally
        {
            log?.Dispose();
        }
    }

    [Fact]
    public async Task each_try_reloads_the_trial_folder_so_weave_reads_the_new_draft()
    {
        _openCode.Agents = _ => [Agent("loom", "[weave-managed]")];

        await _weave.CheckAsync(Owner, WeaveFlavor.Weave, new Dictionary<string, string> { ["config.weave"] = "one" }, CancellationToken.None);
        await _weave.CheckAsync(Owner, WeaveFlavor.Weave, new Dictionary<string, string> { ["config.weave"] = "two" }, CancellationToken.None);

        var project = WeaveConfigFolder.TrialProjectFor(_data, Owner);
        _openCode.Disposed.Count(directory => directory == project).ShouldBe(2);
    }

    [Fact]
    public async Task a_save_reloads_idle_folders_now_and_leaves_busy_ones_waiting()
    {
        var environment = new Dictionary<string, string> { [WeaveEnvironment.GlobalConfigDir] = UserFolder };
        await LeaseAsync(environment, "/work/alpha");
        await LeaseAsync(environment, "/work/beta");
        _openCode.Busy["/work/beta"] = true;
        await _configs.SaveAsync(new WeaveConfig { Source = WeaveConfigSource.Fleet, Files = new Dictionary<string, string> { ["config.weave"] = "x" } });

        await _weave.ConfigChangedAsync(Owner, CancellationToken.None);

        _openCode.Disposed.ShouldBe(["/work/alpha"]);
        _weave.GetApplyStatus(Owner)!.Folders.ShouldBe([new WeaveApplyFolder("/work/alpha", true), new WeaveApplyFolder("/work/beta", false)]);
        File.ReadAllText(Path.Combine(UserFolder, "config.weave")).ShouldBe("x");
    }

    [Fact]
    public async Task a_busy_folder_reloads_once_its_turn_ends()
    {
        var environment = new Dictionary<string, string> { [WeaveEnvironment.GlobalConfigDir] = UserFolder };
        await LeaseAsync(environment, "/work/beta");
        _openCode.Busy["/work/beta"] = true;

        await _weave.ConfigChangedAsync(Owner, CancellationToken.None);
        _openCode.Busy["/work/beta"] = false;

        var deadline = DateTimeOffset.UtcNow + OpenCodeWeave.ApplyPollInterval * 4;
        while (!_weave.GetApplyStatus(Owner)!.Folders.Single().Reloaded && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);
        _weave.GetApplyStatus(Owner)!.Folders.Single().Reloaded.ShouldBeTrue();
        _openCode.Disposed.ShouldBe(["/work/beta"]);
    }

    [Fact]
    public async Task processes_that_dont_read_the_users_folder_are_not_touched()
    {
        await LeaseAsync(new Dictionary<string, string>(), "/work/alpha");
        await LeaseAsync(new Dictionary<string, string> { [WeaveEnvironment.GlobalConfigDir] = "/someone/elses" }, "/work/beta");

        await _weave.ConfigChangedAsync(Owner, CancellationToken.None);

        _openCode.Disposed.ShouldBeEmpty();
        _weave.GetApplyStatus(Owner)!.Folders.ShouldBeEmpty();
    }

    [Fact]
    public async Task fleets_own_working_folder_from_warm_up_is_not_loaded_by_a_reload()
    {
        await LeaseAsync(new Dictionary<string, string> { [WeaveEnvironment.LegacyConfigDir] = UserFolder }, Environment.CurrentDirectory);

        await _weave.ConfigChangedAsync(Owner, CancellationToken.None);

        _openCode.Disposed.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("@weaveio/weave-adapter-opencode", "@weaveio/weave-adapter-opencode")]
    [InlineData("@weaveio/weave-adapter-opencode@0.2.0-next.1", "@weaveio/weave-adapter-opencode")]
    [InlineData("@opencode_weave/weave@latest", "@opencode_weave/weave")]
    [InlineData("opencode-plugin@1.0.0", "opencode-plugin")]
    [InlineData("file:///nowhere/at/all/plugin.js", null)]
    public void a_plugin_entry_names_its_package_without_the_version(string entry, string? package)
    {
        OpenCodeWeave.PackageName(entry).ShouldBe(package);
    }

    [Theory]
    [InlineData("Orchestrates [weave-managed]", false, WeaveFlavor.Weave, true)]
    [InlineData("Orchestrates [weave-managed]", false, WeaveFlavor.Legacy, false)]
    [InlineData("Loom (Main Orchestrator)", false, WeaveFlavor.Legacy, true)]
    [InlineData("The default agent.", true, WeaveFlavor.Legacy, false)]
    public void weave_agents_are_told_apart_by_their_marker_and_legacys_by_not_being_opencodes_own(
        string description, bool native, WeaveFlavor flavor, bool expected)
    {
        OpenCodeWeave.IsFlavorAgent(Agent("a", description, native), flavor).ShouldBe(expected);
    }

    /// <summary>OpenCode's HTTP API, as far as the Weave support uses it.</summary>
    private sealed class FakeOpenCode : HttpMessageHandler
    {
        public IReadOnlyList<string> Plugins { get; set; } = [];

        /// <summary>The agents <c>GET /agent</c> lists for a folder.</summary>
        public Func<string, IReadOnlyList<OpenCodeAgentInfo>> Agents { get; set; } = _ => [];

        /// <summary>Folders with a busy session.</summary>
        public ConcurrentDictionary<string, bool> Busy { get; } = new(StringComparer.Ordinal);

        /// <summary>Folders <c>POST /instance/dispose</c> was called for, in order.</summary>
        public List<string> Disposed { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var directory = Uri.UnescapeDataString(request.RequestUri!.Query.Split("directory=", 2)[1]);
            var body = request.RequestUri.AbsolutePath switch
            {
                "/config" => JsonSerializer.Serialize(new { plugin = Plugins }),
                "/agent" => JsonSerializer.Serialize(Agents(directory).Select(agent => new
                {
                    name = agent.Name,
                    description = agent.Description,
                    native = agent.Native,
                })),
                "/session/status" => Busy.TryGetValue(directory, out var busy) && busy
                    ? """{ "oc-1": { "type": "busy" } }"""
                    : """{ "oc-1": { "type": "idle" } }""",
                "/instance/dispose" => Dispose(directory),
                var path => throw new InvalidOperationException($"Unexpected request {request.Method} {path}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        private string Dispose(string directory)
        {
            lock (Disposed)
                Disposed.Add(directory);
            return "true";
        }
    }
}
