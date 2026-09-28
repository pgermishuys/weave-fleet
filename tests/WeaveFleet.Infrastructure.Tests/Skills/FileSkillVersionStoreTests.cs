using WeaveFleet.Application.Skills;
using WeaveFleet.Infrastructure.Skills;

namespace WeaveFleet.Infrastructure.Tests.Skills;

/// <summary>The user's versions of built-in skills, kept where Fleet's startup sync never writes.</summary>
public sealed class FileSkillVersionStoreTests : IDisposable
{
    private const string User = "local-user";
    private const string Name = "fleet-code-review";
    private const string Fleet = "---\nname: fleet-code-review\ndescription: Reviews.\n---\n\nFleet's way.\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-skill-versions-{Guid.NewGuid():N}");
    private readonly FileSkillVersionStore _store;

    public FileSkillVersionStoreTests() => _store = new FileSkillVersionStore(_root);

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_skill_with_no_versions_is_empty_and_sessions_get_Fleets()
    {
        var history = await _store.GetAsync(User, Name);

        history.ShouldBe(SkillVersionHistory.Empty(Name));
        history.ActiveVersion.ShouldBeNull();
        (await _store.ListAsync(User)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_version_counts_up_becomes_the_active_one_and_keeps_why_it_was_made()
    {
        var first = await _store.AddAsync(User, Name, Mine("one"), new SkillVersionSource(" No style nits. ", "session-1", "Review auth"), Fleet);
        var second = await _store.AddAsync(User, Name, Mine("two"), new SkillVersionSource(null, null, null), Fleet);

        first.Number.ShouldBe(1);
        first.Note.ShouldBe("No style nits.");
        first.SessionId.ShouldBe("session-1");
        first.SessionTitle.ShouldBe("Review auth");
        first.FleetHash.ShouldBe(SkillVersions.Hash(Fleet));
        second.Number.ShouldBe(2);
        second.Note.ShouldBeNull();

        var history = await _store.GetAsync(User, Name);
        history.Active.ShouldBe(2);
        history.Versions.Select(v => v.Number).ShouldBe([1, 2]);
        (await _store.ReadAsync(User, Name, 1)).ShouldBe(Mine("one"));
        (await _store.ReadAsync(User, Name, 2)).ShouldBe(Mine("two"));
        (await _store.ListAsync(User)).Select(h => h.Name).ShouldBe([Name]);
    }

    [Fact]
    public async Task A_version_is_a_folder_a_harness_loads_with_the_skill_in_a_folder_of_its_name()
    {
        await _store.AddAsync(User, Name, Mine("one"), new SkillVersionSource(null, null, null), Fleet);

        var folder = _store.FolderFor(User, Name, 1);

        File.ReadAllText(Path.Combine(folder, Name, "SKILL.md")).ShouldBe(Mine("one"));
        _store.FolderFor(User, Name, 2).ShouldNotBe(folder);
    }

    [Fact]
    public async Task Going_back_to_Fleets_or_an_older_version_keeps_every_version()
    {
        await _store.AddAsync(User, Name, Mine("one"), new SkillVersionSource(null, null, null), Fleet);
        await _store.AddAsync(User, Name, Mine("two"), new SkillVersionSource(null, null, null), Fleet);

        await _store.SetActiveAsync(User, Name, null);
        (await _store.GetAsync(User, Name)).Active.ShouldBeNull();

        await _store.SetActiveAsync(User, Name, 1);
        var history = await _store.GetAsync(User, Name);
        history.Active.ShouldBe(1);
        history.Versions.Count.ShouldBe(2);

        await Should.ThrowAsync<ArgumentException>(() => _store.SetActiveAsync(User, Name, 7));
    }

    [Fact]
    public async Task Fleets_version_is_kept_so_a_later_change_to_it_can_be_shown_and_keeping_mine_moves_the_baseline()
    {
        await _store.AddAsync(User, Name, Mine("one"), new SkillVersionSource(null, null, null), Fleet);
        var updated = Fleet.Replace("Fleet's way.", "Fleet's better way.", StringComparison.Ordinal);

        var before = await _store.GetAsync(User, Name);
        before.FleetBaseline.ShouldBe(SkillVersions.Hash(Fleet));
        (await _store.ReadFleetAsync(User, Name, before.FleetBaseline!)).ShouldBe(Fleet);

        await _store.KeepOverAsync(User, Name, updated);

        var after = await _store.GetAsync(User, Name);
        after.FleetBaseline.ShouldBe(SkillVersions.Hash(updated));
        (await _store.ReadFleetAsync(User, Name, after.FleetBaseline!)).ShouldBe(updated);

        // A new version is based on Fleet's current one, which replaces what was kept over.
        await _store.AddAsync(User, Name, Mine("two"), new SkillVersionSource(null, null, null), updated);
        (await _store.GetAsync(User, Name)).KeptFleetHash.ShouldBeNull();
    }

    [Fact]
    public async Task Each_user_has_versions_of_their_own()
    {
        await _store.AddAsync("owner-1", Name, Mine("one"), new SkillVersionSource(null, null, null), Fleet);

        (await _store.GetAsync("owner-2", Name)).Versions.ShouldBeEmpty();
        _store.FolderFor("owner-1", Name, 1).ShouldNotBe(_store.FolderFor("owner-2", Name, 1));
    }

    [Fact]
    public async Task A_broken_index_means_Fleets_version_until_it_is_fixed()
    {
        await _store.AddAsync(User, Name, Mine("one"), new SkillVersionSource(null, null, null), Fleet);
        var index = Directory.GetFiles(_root, "skill.json", SearchOption.AllDirectories).Single();
        File.WriteAllText(index, "{ not json");

        (await _store.GetAsync(User, Name)).Active.ShouldBeNull();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("Fleet-Code-Review")]
    [InlineData("")]
    public async Task A_name_that_isnt_a_skill_name_never_names_a_path(string name)
    {
        await Should.ThrowAsync<ArgumentException>(() => _store.AddAsync(User, name, Mine("one"), new SkillVersionSource(null, null, null), Fleet));
        (await _store.GetAsync(User, name)).Versions.ShouldBeEmpty();
        (await _store.ReadAsync(User, name, 1)).ShouldBeNull();
    }

    private static string Mine(string text) => $"---\nname: {Name}\ndescription: Reviews.\n---\n\n{text}\n";
}
