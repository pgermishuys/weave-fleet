using Shouldly;
using WeaveFleet.Application.Skills;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class BuiltInSkillServiceTests
{
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly BuiltInSkillService _service;

    public BuiltInSkillServiceTests()
    {
        _service = new BuiltInSkillService(new Catalog(), _preferences);
    }

    [Fact]
    public async Task ListAsync_shows_every_skill_off_until_the_user_turns_it_on()
    {
        var skills = await _service.ListAsync();

        skills.ShouldBe([
            new BuiltInSkillView("fleet-code-review", "Reviews.", Enabled: false),
            new BuiltInSkillView("fleet-simplify", "Simplifies.", Enabled: false),
        ]);
    }

    [Fact]
    public async Task SetEnabledAsync_turns_one_skill_on_and_off_and_keeps_the_others()
    {
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-simplify,retired-skill");

        var on = await _service.SetEnabledAsync("fleet-code-review", enabled: true);

        on.Value.ShouldBe(new BuiltInSkillView("fleet-code-review", "Reviews.", Enabled: true));
        (await _preferences.GetAsync(BuiltInSkillService.PreferenceKey)).ShouldBe("fleet-code-review,fleet-simplify,retired-skill");

        await _service.SetEnabledAsync("fleet-simplify", enabled: false);

        (await _preferences.GetAsync(BuiltInSkillService.PreferenceKey)).ShouldBe("fleet-code-review,retired-skill");
        (await _service.ListAsync()).Select(skill => skill.Enabled).ShouldBe([true, false]);
    }

    [Fact]
    public async Task SetEnabledAsync_tells_every_harness_whose_choice_changed()
    {
        var registry = new FakeHarnessRegistry();
        var opencode = new FakeHarnessRuntime("opencode");
        var opencode2 = new FakeHarnessRuntime("opencode2");
        registry.Register(new FakeHarness("opencode", "OpenCode"));
        registry.Register(new FakeHarness("opencode2", "OpenCode 2"));
        registry.Register(opencode);
        registry.Register(opencode2);
        var service = new BuiltInSkillService(new Catalog(), _preferences, registry, new TestUserContext("owner-1"));

        await service.SetEnabledAsync("fleet-simplify", enabled: true);
        await service.SetEnabledAsync("not-shipped", enabled: true);

        opencode.BuiltInSkillChanges.ShouldBe(["owner-1"]);
        opencode2.BuiltInSkillChanges.ShouldBe(["owner-1"]);
    }

    [Fact]
    public async Task SetEnabledAsync_refuses_a_skill_Fleet_doesnt_ship()
    {
        var result = await _service.SetEnabledAsync("code-review", enabled: true);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("BuiltInSkill.NotFound");
        (await _preferences.GetAsync(BuiltInSkillService.PreferenceKey)).ShouldBeNull();
    }

    // ── The user's own versions ─────────────────────────────────────────────

    [Fact]
    public async Task SaveVersionAsync_makes_the_users_version_the_one_sessions_get_and_keeps_why()
    {
        var (service, _, sessions) = WithVersions();
        sessions.Seed(new WeaveFleet.Domain.Entities.Session { Id = "s-1", Title = "Review auth refactor" });

        var saved = await service.SaveVersionAsync("fleet-code-review", Mine("No style nits."), " Style isn't a bug. ", "s-1");

        saved.IsSuccess.ShouldBeTrue();
        saved.Value.Version.ShouldBe(1);
        saved.Value.YourContent.ShouldBe(Mine("No style nits."));
        saved.Value.FleetContent.ShouldBe(Catalog.Fleet("fleet-code-review"));
        saved.Value.Versions.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            v => v.Number.ShouldBe(1),
            v => v.Note.ShouldBe("Style isn't a bug."),
            v => v.SessionTitle.ShouldBe("Review auth refactor"),
            v => v.Active.ShouldBeTrue());
        (await service.ListAsync())[0].ShouldBe(new BuiltInSkillView("fleet-code-review", "Reviews.", Enabled: false, Version: 1, VersionCount: 1));
    }

    [Fact]
    public async Task SaveVersionAsync_refuses_a_skill_that_renames_itself_or_changes_nothing()
    {
        var (service, _, _) = WithVersions();

        var renamed = await service.SaveVersionAsync("fleet-code-review", Mine("x").Replace("name: fleet-code-review", "name: my-review", StringComparison.Ordinal), null, null);
        renamed.Error.Description.ShouldBe("Keep the name in the front matter as fleet-code-review: it's how sessions find the skill.");

        var noFrontMatter = await service.SaveVersionAsync("fleet-code-review", "Just some text.", null, null);
        noFrontMatter.Error.Code.ShouldBe("Validation.Content");

        var same = await service.SaveVersionAsync("fleet-code-review", Catalog.Fleet("fleet-code-review").ReplaceLineEndings("\r\n"), null, null);
        same.Error.Description.ShouldBe("Nothing changed: this is the text sessions get already.");
    }

    [Fact]
    public async Task UseVersionAsync_goes_back_to_Fleets_or_an_older_version_and_tells_the_harnesses()
    {
        var (service, runtime, _) = WithVersions();
        await service.SaveVersionAsync("fleet-code-review", Mine("one"), null, null);
        await service.SaveVersionAsync("fleet-code-review", Mine("two"), null, null);
        runtime.BuiltInSkillChanges.Clear();

        var fleets = await service.UseVersionAsync("fleet-code-review", null);
        fleets.Value.Version.ShouldBeNull();
        fleets.Value.YourContent.ShouldBeNull();
        fleets.Value.Versions.Count.ShouldBe(2);

        var older = await service.UseVersionAsync("fleet-code-review", 1);
        older.Value.YourContent.ShouldBe(Mine("one"));
        runtime.BuiltInSkillChanges.ShouldBe(["owner-1", "owner-1"]);

        (await service.UseVersionAsync("fleet-code-review", 9)).Error.Code.ShouldBe("SkillVersion.NotFound");
    }

    [Fact]
    public async Task When_Fleet_changes_its_version_the_skill_says_so_until_the_user_keeps_theirs()
    {
        var catalog = new Catalog();
        var (service, _, _) = WithVersions(catalog);
        await service.SaveVersionAsync("fleet-code-review", Mine("one"), null, null);
        var before = catalog.Contents["fleet-code-review"];
        catalog.Contents["fleet-code-review"] = before + "\nA new rule.\n";

        var changed = await service.GetAsync("fleet-code-review");
        changed.Value.FleetChanged.ShouldBeTrue();
        changed.Value.FleetBefore.ShouldBe(before);
        (await service.ListAsync())[0].FleetChanged.ShouldBeTrue();

        var kept = await service.KeepMineAsync("fleet-code-review");
        kept.Value.FleetChanged.ShouldBeFalse();
        kept.Value.FleetBefore.ShouldBeNull();
        kept.Value.Version.ShouldBe(1);
    }

    [Fact]
    public async Task A_skill_using_Fleets_version_never_says_Fleet_changed_it()
    {
        var catalog = new Catalog();
        var (service, _, _) = WithVersions(catalog);
        await service.SaveVersionAsync("fleet-code-review", Mine("one"), null, null);
        await service.UseVersionAsync("fleet-code-review", null);
        catalog.Contents["fleet-code-review"] += "\nA new rule.\n";

        (await service.GetAsync("fleet-code-review")).Value.FleetChanged.ShouldBeFalse();
    }

    private (BuiltInSkillService Service, FakeHarnessRuntime Runtime, InMemorySessionRepository Sessions) WithVersions(Catalog? catalog = null)
    {
        var registry = new FakeHarnessRegistry();
        var runtime = new FakeHarnessRuntime("opencode");
        registry.Register(new FakeHarness("opencode", "OpenCode"));
        registry.Register(runtime);
        var sessions = new InMemorySessionRepository();
        var service = new BuiltInSkillService(
            catalog ?? new Catalog(), _preferences, registry, new TestUserContext("owner-1"), versions: new InMemorySkillVersionStore(), sessions: sessions);
        return (service, runtime, sessions);
    }

    private static string Mine(string body) => $"---\nname: fleet-code-review\ndescription: Mine.\n---\n\n{body}\n";

    private sealed class Catalog : IBuiltInSkillCatalog
    {
        public IReadOnlyList<BuiltInSkill> Skills { get; } =
        [
            new("fleet-code-review", "Reviews."),
            new("fleet-simplify", "Simplifies."),
        ];

        public Dictionary<string, string> Contents { get; } = new()
        {
            ["fleet-code-review"] = Fleet("fleet-code-review"),
            ["fleet-simplify"] = Fleet("fleet-simplify"),
        };

        public string? ContentOf(string name) => Contents.GetValueOrDefault(name);

        public static string Fleet(string name) => $"---\nname: {name}\ndescription: Fleet's.\n---\n\nFleet's way.\n";
    }
}
