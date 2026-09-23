using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Weave;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Weave;

public sealed class WeaveConfigServiceTests
{
    private static readonly WeaveInstall Weave = new(WeaveFlavor.Weave, "@weaveio/weave-adapter-opencode", "@weaveio/weave-adapter-opencode@0.2.0-next.1", true);
    private static readonly WeaveInstall Legacy = new(WeaveFlavor.Legacy, "@opencode_weave/weave", "@opencode_weave/weave@0.9.0", true);

    private readonly FakeHarnessRegistry _registry = new();
    private readonly FakeHarnessRuntime _opencode = new("opencode");
    private readonly FakeHarnessRuntime _pi = new("pi");
    private readonly InMemoryWeaveConfigRepository _configs = new();
    private readonly WeaveDetectionCache _detections = new();

    public WeaveConfigServiceTests()
    {
        _registry.Register(new FakeHarness("opencode", "OpenCode"));
        _registry.Register(_opencode);
        _registry.Register(new FakeHarness("pi", "Pi"));
        _registry.Register(_pi);
        _opencode.WeaveInstalls = [Weave];
    }

    private WeaveConfigService CreateService(FleetOptions? options = null) => new(
        _configs, _registry, new TestUserContext("user-1"), options ?? new FleetOptions(), _detections, TimeProvider.System,
        NullLogger<WeaveConfigService>.Instance);

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
}
