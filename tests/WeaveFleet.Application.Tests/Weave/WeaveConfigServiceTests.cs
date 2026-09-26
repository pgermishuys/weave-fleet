using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Weave;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Weave;

public sealed class WeaveConfigServiceTests : IDisposable
{
    private static readonly WeaveInstall Weave = new(WeaveFlavor.Weave, "@weaveio/weave-adapter-opencode", "@weaveio/weave-adapter-opencode@0.2.0-next.1", true);
    private static readonly WeaveInstall Legacy = new(WeaveFlavor.Legacy, "@opencode_weave/weave", "@opencode_weave/weave@0.9.0", true);

    private readonly FakeHarnessRegistry _registry = new();
    private readonly FakeHarnessRuntime _opencode = new("opencode");
    private readonly FakeHarnessRuntime _pi = new("pi");
    private readonly InMemoryWeaveConfigRepository _configs = new();
    private readonly WeaveDetectionCache _detections = new();
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly FakeVersions _versions = new();
    private readonly string _configFolder = Path.Combine(Path.GetTempPath(), $"weave-plugin-{Guid.NewGuid():N}");

    public WeaveConfigServiceTests()
    {
        _registry.Register(new FakeHarness("opencode", "OpenCode"));
        _registry.Register(_opencode);
        _registry.Register(new FakeHarness("pi", "Pi"));
        _registry.Register(_pi);
        _opencode.WeaveInstalls = [Weave];
    }

    public void Dispose()
    {
        if (Directory.Exists(_configFolder))
            Directory.Delete(_configFolder, recursive: true);
    }

    private WeaveConfigService CreateService(FleetOptions? options = null) => new(
        _configs, _registry, new TestUserContext("user-1"), options ?? new FleetOptions(), _detections, TimeProvider.System,
        NullLogger<WeaveConfigService>.Instance, _preferences, _versions);

    private sealed class FakeVersions : IWeavePackageVersions
    {
        public string? Version { get; set; } = "0.2.0-next.3";

        public List<string> Asked { get; } = [];

        public Task<string?> NewestAsync(string package, CancellationToken ct)
        {
            Asked.Add(package);
            return Task.FromResult(Version);
        }
    }

    private const string V2Package = "@weaveio/weave-adapter-opencode2";
    private const string V2Entry = V2Package + "@0.2.0-next.3";

    /// <summary>OpenCode with no Weave yet, whose config lives in the test's folder; a plugin change makes it load what's in the file.</summary>
    private void WithoutWeave()
    {
        _opencode.WeaveInstalls = [];
        _opencode.WeavePluginHome = new WeavePluginHome(_configFolder, ["opencode.jsonc", "opencode.json"], "plugins", V2Package);
        _opencode.OnWeavePluginsChanged = () =>
        {
            var file = Path.Combine(_configFolder, "opencode.json");
            var listed = File.Exists(file) ? WeavePluginList.Read(File.ReadAllText(file), "plugins") : [];
            _opencode.WeaveInstalls = listed.Where(entry => entry.StartsWith(V2Package, StringComparison.Ordinal))
                .Select(entry => new WeaveInstall(WeaveFlavor.Weave, V2Package, entry, true))
                .ToList();
        };
    }

    private static Dictionary<string, string> Files(params (string Path, string Content)[] files) =>
        files.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal);

    [Fact]
    public async Task nothing_saved_means_the_users_own_files()
    {
        var view = (await CreateService().GetAsync(redetect: false, CancellationToken.None)).Value;

        view.Source.ShouldBe("own");
        view.Files.ShouldBeEmpty();
        view.UpdatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task each_harness_says_which_weave_it_loads_and_one_that_cant_take_a_config_says_so()
    {
        var view = (await CreateService().GetAsync(redetect: false, CancellationToken.None)).Value;

        var opencode = view.Harnesses.Single(h => h.HarnessType == "opencode");
        opencode.Checked.ShouldBeTrue();
        opencode.Installs.ShouldBe([Weave]);
        var pi = view.Harnesses.Single(h => h.HarnessType == "pi");
        pi.Checked.ShouldBeFalse();
        pi.Note.ShouldBe("Fleet doesn't hand Weave a config in Pi yet.");
    }

    [Fact]
    public async Task harnesses_that_arent_set_up_are_left_out()
    {
        var registry = new FakeHarnessRegistry();
        registry.Register(new FakeHarness("opencode", "OpenCode"));
        registry.Register(new FakeHarnessRuntime("opencode", available: false));
        var service = new WeaveConfigService(_configs, registry, new TestUserContext("user-1"), new FleetOptions(), _detections,
            TimeProvider.System, NullLogger<WeaveConfigService>.Instance);

        var view = (await service.GetAsync(redetect: false, CancellationToken.None)).Value;

        view.Harnesses.ShouldBeEmpty();
    }

    [Fact]
    public async Task what_the_harnesses_said_is_kept_until_asked_again()
    {
        var service = CreateService();
        await service.GetAsync(redetect: false, CancellationToken.None);
        _opencode.WeaveInstalls = [Legacy];

        (await service.GetAsync(redetect: false, CancellationToken.None)).Value
            .Harnesses.Single(h => h.HarnessType == "opencode").Installs.ShouldBe([Weave]);
        (await service.GetAsync(redetect: true, CancellationToken.None)).Value
            .Harnesses.Single(h => h.HarnessType == "opencode").Installs.ShouldBe([Legacy]);
    }

    [Fact]
    public async Task saving_a_fleet_config_has_the_harness_try_it_first_and_then_hands_it_over()
    {
        var files = Files(("config.weave", "disable agents [\"warp\"]\n"), ("prompts/team.md", "Be brief."));

        var result = (await CreateService().SaveAsync("fleet", files, CancellationToken.None)).Value;

        result.Saved.ShouldBeTrue();
        result.Checks.Single().Check.Ok.ShouldBeTrue();
        _opencode.WeaveChecks.Single().Flavor.ShouldBe(WeaveFlavor.Weave);
        _opencode.WeaveChecks.Single().Files.ShouldBe(files);
        _configs.Saved!.Source.ShouldBe(WeaveConfigSource.Fleet);
        _configs.Saved.Files.ShouldBe(files);
        _opencode.WeaveChanges.ShouldBe(["user-1"]);
        result.Config!.Source.ShouldBe("fleet");
    }

    [Fact]
    public async Task a_config_that_leaves_weave_with_no_agents_is_not_saved()
    {
        _opencode.WeaveCheckResult = new WeaveCheck(false, [], "OpenCode started, but Weave added no agents.", ["config.weave:2:1 UnclosedBlock"]);

        var result = (await CreateService().SaveAsync("fleet", Files(("config.weave", "agent a {")), CancellationToken.None)).Value;

        result.Saved.ShouldBeFalse();
        result.Config.ShouldBeNull();
        result.Checks.Single().Check.Details.ShouldBe(["config.weave:2:1 UnclosedBlock"]);
        _configs.Saved.ShouldBeNull();
        _opencode.WeaveChanges.ShouldBeEmpty();
    }

    [Fact]
    public async Task broken_legacy_json_is_caught_with_its_line_before_any_harness_starts()
    {
        _opencode.WeaveInstalls = [Legacy];

        var result = (await CreateService().SaveAsync("fleet", Files(("weave-opencode.jsonc", "{\n  \"disabled_agents\": [\"warp\"\n}\n")), CancellationToken.None)).Value;

        result.Saved.ShouldBeFalse();
        result.Checks.Single().Check.Error.ShouldBe("weave-opencode.jsonc: line 3, column 1: this isn't valid JSON.");
        _opencode.WeaveChecks.ShouldBeEmpty();
    }

    [Fact]
    public async Task legacy_json_with_comments_and_trailing_commas_is_tried_by_the_harness()
    {
        _opencode.WeaveInstalls = [Legacy];

        var result = (await CreateService().SaveAsync("fleet", Files(("weave-opencode.jsonc", "{\n  // mine\n  \"disabled_agents\": [\"warp\",],\n}\n")), CancellationToken.None)).Value;

        result.Saved.ShouldBeTrue();
        _opencode.WeaveChecks.Single().Flavor.ShouldBe(WeaveFlavor.Legacy);
    }

    [Fact]
    public async Task a_weave_no_harness_can_take_a_config_for_is_saved_without_a_check()
    {
        _opencode.WeaveInstalls = [Weave with { AcceptsFleetConfig = false }];

        var result = (await CreateService().SaveAsync("fleet", Files(("config.weave", "")), CancellationToken.None)).Value;

        result.Saved.ShouldBeTrue();
        result.Checks.ShouldBeEmpty();
        _opencode.WeaveChecks.ShouldBeEmpty();
    }

    [Fact]
    public async Task switching_back_to_the_users_own_files_keeps_fleets_files_without_trying_them()
    {
        var files = Files(("config.weave", "agent a {"));

        var result = (await CreateService().SaveAsync("own", files, CancellationToken.None)).Value;

        result.Saved.ShouldBeTrue();
        _opencode.WeaveChecks.ShouldBeEmpty();
        _configs.Saved!.Source.ShouldBe(WeaveConfigSource.Own);
        _configs.Saved.Files.ShouldBe(files);
        _opencode.WeaveChanges.ShouldBe(["user-1"]);
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("../config.weave")]
    [InlineData("prompts/../../etc.md")]
    [InlineData("prompts/sub/team.md")]
    [InlineData("prompts/team.txt")]
    [InlineData("prompts/.hidden.md")]
    public async Task only_weaves_config_and_prompt_files_can_be_kept(string path)
    {
        var result = await CreateService().SaveAsync("fleet", Files((path, "x")), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Weave.Files");
        _configs.Saved.ShouldBeNull();
    }

    [Fact]
    public async Task an_unknown_source_is_refused()
    {
        var result = await CreateService().SaveAsync("theirs", Files(), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Weave.Source");
    }

    [Fact]
    public async Task trying_a_draft_needs_that_weaves_config_file()
    {
        var result = await CreateService().CheckAsync(WeaveFlavor.Legacy, Files(("config.weave", "")), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("There's no weave-opencode.jsonc to try.");
    }

    [Fact]
    public async Task the_last_saves_progress_comes_from_the_harness()
    {
        _opencode.WeaveApplyStatus = new WeaveApplyStatus([new WeaveApplyFolder("/work/alpha", true), new WeaveApplyFolder("/work/beta", false)]);

        var view = (await CreateService().GetAsync(redetect: false, CancellationToken.None)).Value;

        view.Apply!.Folders.Select(folder => (folder.Directory, folder.Reloaded)).ShouldBe([("/work/alpha", true), ("/work/beta", false)]);
    }

    [Fact]
    public void the_users_own_files_arent_read_when_fleet_runs_with_sign_in()
    {
        var options = new FleetOptions();
        options.Auth.Enabled = true;

        var result = CreateService(options).ReadOwn(WeaveFlavor.Weave);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Weave.Own");
    }

    [Fact]
    public async Task a_harness_without_weave_says_which_file_add_weave_would_write()
    {
        WithoutWeave();

        var view = (await CreateService().GetAsync(redetect: true, CancellationToken.None)).Value;

        view.Harnesses.Single(h => h.HarnessType == "opencode").AddTo.ShouldBe(Path.Combine(_configFolder, "opencode.json"));
        view.Harnesses.Single(h => h.HarnessType == "pi").AddTo.ShouldBeNull();
    }

    [Fact]
    public async Task add_weave_writes_the_newest_adapter_into_the_harnesses_config_and_the_harness_loads_it()
    {
        WithoutWeave();
        Directory.CreateDirectory(_configFolder);
        await File.WriteAllTextAsync(Path.Combine(_configFolder, "opencode.json"), """
            {
              // mine
              "model": "anthropic/claude"
            }
            """);

        var result = await CreateService().AddPluginAsync("opencode", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        _versions.Asked.ShouldBe([V2Package]);
        (await File.ReadAllTextAsync(Path.Combine(_configFolder, "opencode.json"))).ShouldBe($$"""
            {
              "plugins": ["{{V2Entry}}"],
              // mine
              "model": "anthropic/claude"
            }
            """);
        _opencode.WeavePluginChanges.ShouldBe(["user-1"]);
        result.Value.Loaded.ShouldBeTrue();
        result.Value.Entry.ShouldBe(V2Entry);
        result.Value.Message.ShouldBe($"Added {V2Entry} to {Path.Combine(_configFolder, "opencode.json")}. OpenCode loaded it.");
        var install = result.Value.Config.Harnesses.Single(h => h.HarnessType == "opencode").Installs.ShouldHaveSingleItem();
        install.AddedByFleet.ShouldBeTrue();
        result.Value.Config.Harnesses.Single(h => h.HarnessType == "opencode").AddTo.ShouldBeNull();
    }

    [Fact]
    public async Task add_weave_creates_the_config_when_the_harness_has_none()
    {
        WithoutWeave();

        var result = await CreateService().AddPluginAsync("opencode", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        WeavePluginList.Read(await File.ReadAllTextAsync(Path.Combine(_configFolder, "opencode.json")), "plugins").ShouldBe([V2Entry]);
    }

    [Fact]
    public async Task an_adapter_the_harness_couldnt_load_says_why()
    {
        WithoutWeave();
        _opencode.OnWeavePluginsChanged = () => _opencode.WeaveInstalls =
            [new WeaveInstall(WeaveFlavor.Weave, V2Package, V2Entry, false, "No matching version found")];

        var result = await CreateService().AddPluginAsync("opencode", CancellationToken.None);

        result.Value.Loaded.ShouldBeFalse();
        result.Value.Message.ShouldBe($"Added {V2Entry} to {Path.Combine(_configFolder, "opencode.json")}, but OpenCode couldn't load it: No matching version found");
    }

    [Fact]
    public async Task add_weave_is_refused_when_the_harness_already_has_weave()
    {
        _opencode.WeavePluginHome = new WeavePluginHome(_configFolder, ["opencode.json"], "plugins", V2Package);

        var result = await CreateService().AddPluginAsync("opencode", CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("General.Conflict");
        result.Error.Description.ShouldBe("OpenCode already has Weave in its plugin list.");
        Directory.Exists(_configFolder).ShouldBeFalse();
    }

    [Fact]
    public async Task add_weave_doesnt_guess_between_two_config_files()
    {
        WithoutWeave();
        Directory.CreateDirectory(_configFolder);
        await File.WriteAllTextAsync(Path.Combine(_configFolder, "opencode.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_configFolder, "opencode.jsonc"), "{}");

        var service = CreateService();

        (await service.GetAsync(redetect: true, CancellationToken.None)).Value.Harnesses.Single(h => h.HarnessType == "opencode").AddTo.ShouldBe(_configFolder);
        var result = await service.AddPluginAsync("opencode", CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("has opencode.jsonc and opencode.json");
        (await File.ReadAllTextAsync(Path.Combine(_configFolder, "opencode.json"))).ShouldBe("{}");
    }

    [Fact]
    public async Task an_entry_the_harness_didnt_load_is_not_added_twice()
    {
        WithoutWeave();
        Directory.CreateDirectory(_configFolder);
        await File.WriteAllTextAsync(Path.Combine(_configFolder, "opencode.json"), $$"""{ "plugins": ["{{V2Package}}@9.9.9"] }""");

        var result = await CreateService().AddPluginAsync("opencode", CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldStartWith($"{Path.Combine(_configFolder, "opencode.json")} already lists {V2Package}@9.9.9, but OpenCode didn't load it.");
    }

    [Fact]
    public async Task add_weave_needs_npm_to_pick_the_version()
    {
        WithoutWeave();
        _versions.Version = null;

        var result = await CreateService().AddPluginAsync("opencode", CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe($"Fleet couldn't look up {V2Package} on npm. Check the connection and try again.");
        File.Exists(Path.Combine(_configFolder, "opencode.json")).ShouldBeFalse();
    }

    [Fact]
    public async Task a_harness_fleet_cant_add_weave_to_says_so()
    {
        _pi.WeaveInstalls = [];

        var result = await CreateService().AddPluginAsync("pi", CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("Fleet can't add Weave to Pi.");
    }

    [Fact]
    public async Task nothing_is_added_when_fleet_runs_with_sign_in()
    {
        WithoutWeave();
        var options = new FleetOptions();
        options.Auth.Enabled = true;
        var service = CreateService(options);

        (await service.GetAsync(redetect: true, CancellationToken.None)).Value.Harnesses.Single(h => h.HarnessType == "opencode").AddTo.ShouldBeNull();
        var result = await service.AddPluginAsync("opencode", CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("Fleet can't change a harness's plugins when it runs with sign-in.");
    }

    [Fact]
    public async Task remove_takes_out_only_what_add_weave_put_in()
    {
        WithoutWeave();
        Directory.CreateDirectory(_configFolder);
        var file = Path.Combine(_configFolder, "opencode.json");
        await File.WriteAllTextAsync(file, """{ "plugins": ["@my/plugin"] }""");
        var service = CreateService();
        (await service.AddPluginAsync("opencode", CancellationToken.None)).IsSuccess.ShouldBeTrue();

        var result = await service.RemovePluginAsync("opencode", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        (await File.ReadAllTextAsync(file)).ShouldBe("""{ "plugins": ["@my/plugin"] }""");
        result.Value.Loaded.ShouldBeTrue();
        result.Value.Message.ShouldBe($"Took {V2Entry} out of {file}.");
        _opencode.WeavePluginChanges.Count.ShouldBe(2);
        result.Value.Config.Harnesses.Single(h => h.HarnessType == "opencode").AddTo.ShouldBe(file);
    }

    [Fact]
    public async Task weave_the_user_added_is_not_fleets_to_remove()
    {
        _opencode.WeavePluginHome = new WeavePluginHome(_configFolder, ["opencode.json"], "plugins", V2Package);

        var view = (await CreateService().GetAsync(redetect: true, CancellationToken.None)).Value;
        var result = await CreateService().RemovePluginAsync("opencode", CancellationToken.None);

        view.Harnesses.Single(h => h.HarnessType == "opencode").Installs.ShouldHaveSingleItem().AddedByFleet.ShouldBeFalse();
        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("Fleet didn't add Weave to OpenCode, so it leaves OpenCode's plugin list alone.");
    }
}
