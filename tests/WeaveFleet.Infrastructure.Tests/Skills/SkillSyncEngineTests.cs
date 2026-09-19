using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Skills;

namespace WeaveFleet.Infrastructure.Tests.Skills;

[Collection("SkillTests")]
public sealed class SkillSyncEngineTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _weaveSkillsDir;
    private readonly string _openCodeSkillsDir;
    private readonly string _claudeCodeSkillsDir;
    private readonly string _repoDir;
    private readonly JsonSkillManifestStore _manifestStore;
    private readonly FakeHarnessPoolRecycler _poolRecycler;
    private readonly SkillSyncEngine _syncEngine;

    public SkillSyncEngineTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"weave-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);

        _weaveSkillsDir = Path.Combine(_testDir, ".weave", "skills");
        _openCodeSkillsDir = Path.Combine(_testDir, ".config", "opencode", "skills");
        _claudeCodeSkillsDir = Path.Combine(_testDir, ".claude", "skills");
        _repoDir = Path.Combine(_testDir, "src", "my-repo");
        Directory.CreateDirectory(_repoDir);

        _manifestStore = new JsonSkillManifestStore(_testDir);
        _poolRecycler = new FakeHarnessPoolRecycler();
        _syncEngine = new SkillSyncEngine(
            _manifestStore, new HarnessInstallPaths(_testDir), NullLogger<SkillSyncEngine>.Instance, _poolRecycler);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    // ── SyncAllAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAllAsync_WhenManifestIsEmpty_ReturnsEmptyResults()
    {
        var results = await _syncEngine.SyncAllAsync();

        results.ShouldBeEmpty();
        _poolRecycler.RecycleCount.ShouldBe(0);
    }

    [Fact]
    public async Task SyncAllAsync_SyncsAllSkillsInManifest_AndRecordsWhereTheyLanded()
    {
        CreateSource("skill-1");
        CreateSource("skill-2");
        await _manifestStore.AddEntryAsync("local-user", null, Entry("skill-1", harnesses: ["opencode"]));
        await _manifestStore.AddEntryAsync("local-user", null, Entry("skill-2", harnesses: ["claude-code"]));

        var results = await _syncEngine.SyncAllAsync();

        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => r.Success);
        results.First(r => r.SkillName == "skill-1").Harness.ShouldBe("opencode");
        results.First(r => r.SkillName == "skill-2").Harness.ShouldBe("claude-code");

        var manifest = await _manifestStore.LoadAsync("local-user");
        manifest.Skills.First(s => s.Name == "skill-1").InstalledPaths.ShouldBe([Path.Combine(_openCodeSkillsDir, "skill-1")]);
        manifest.Skills.First(s => s.Name == "skill-2").InstalledPaths.ShouldBe([Path.Combine(_claudeCodeSkillsDir, "skill-2")]);
        _poolRecycler.RecycleCount.ShouldBe(1);
    }

    [Fact]
    public async Task SyncAllAsync_WhenSomeSkillsFail_StillSyncsOthers()
    {
        CreateSource("good-skill");
        await _manifestStore.AddEntryAsync("local-user", null, Entry("good-skill"));
        await _manifestStore.AddEntryAsync("local-user", null, Entry("bad-skill"));

        var results = await _syncEngine.SyncAllAsync();

        results.Count.ShouldBe(2);
        results.First(r => r.SkillName == "good-skill").Success.ShouldBeTrue();
        var badResult = results.First(r => r.SkillName == "bad-skill");
        badResult.Success.ShouldBeFalse();
        badResult.ErrorMessage!.ShouldContain("Source directory not found");
        _poolRecycler.RecycleCount.ShouldBe(1);
    }

    // ── SyncSkillAsync: where skills land ─────────────────────────────────────

    [Fact]
    public async Task SyncSkillAsync_CopiesSkillIntoGlobalOpenCodeFolder()
    {
        var sourceDir = CreateSource("test-skill");
        Directory.CreateDirectory(Path.Combine(sourceDir, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "scripts", "run.sh"), "echo hi");

        var results = await _syncEngine.SyncSkillAsync(Entry("test-skill"));

        var result = results.ShouldHaveSingleItem();
        result.Success.ShouldBeTrue();
        result.Harness.ShouldBe("opencode");

        var targetPath = Path.Combine(_openCodeSkillsDir, "test-skill");
        result.TargetPath.ShouldBe(targetPath);
        new DirectoryInfo(targetPath).LinkTarget.ShouldBeNull("skills are copied, not linked, on every OS");
        File.ReadAllText(Path.Combine(targetPath, "SKILL.md")).ShouldBe("# test-skill");
        File.Exists(Path.Combine(targetPath, "scripts", "run.sh")).ShouldBeTrue();
        _poolRecycler.RecycleCount.ShouldBe(1);
    }

    [Fact]
    public async Task SyncSkillAsync_DoesNotCopyGitFolderOrLegacyMarker()
    {
        var sourceDir = CreateSource("cloned-skill");
        Directory.CreateDirectory(Path.Combine(sourceDir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, ".git", "HEAD"), "ref: refs/heads/main");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, InstalledFiles.LegacyManagedMarker), "Managed by Weave Fleet");

        await _syncEngine.SyncSkillAsync(Entry("cloned-skill"));

        var targetPath = Path.Combine(_openCodeSkillsDir, "cloned-skill");
        File.Exists(Path.Combine(targetPath, "SKILL.md")).ShouldBeTrue();
        Directory.Exists(Path.Combine(targetPath, ".git")).ShouldBeFalse();
        File.Exists(Path.Combine(targetPath, InstalledFiles.LegacyManagedMarker)).ShouldBeFalse();
    }

    [Fact]
    public async Task SyncSkillAsync_ForProject_CopiesIntoTheRepositoryHarnessFolders()
    {
        CreateSource("repo-skill");

        var results = await _syncEngine.SyncSkillAsync(
            Entry("repo-skill", harnesses: ["opencode", "claude-code"]) with { Scope = InstallScope.Project, ProjectPath = _repoDir });

        results.ShouldAllBe(r => r.Success);
        results.First(r => r.Harness == "opencode").TargetPath.ShouldBe(Path.Combine(_repoDir, ".opencode", "skills", "repo-skill"));
        results.First(r => r.Harness == "claude-code").TargetPath.ShouldBe(Path.Combine(_repoDir, ".claude", "skills", "repo-skill"));
        File.Exists(Path.Combine(_repoDir, ".opencode", "skills", "repo-skill", "SKILL.md")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_openCodeSkillsDir, "repo-skill")).ShouldBeFalse();
    }

    [Fact]
    public async Task SyncSkillAsync_UsesXdgConfigHomeForOpenCode()
    {
        CreateSource("xdg-skill");
        var xdgConfigHome = Path.Combine(_testDir, "xdg");
        var engine = new SkillSyncEngine(
            _manifestStore, new HarnessInstallPaths(_testDir, xdgConfigHome), NullLogger<SkillSyncEngine>.Instance);

        var result = (await engine.SyncSkillAsync(Entry("xdg-skill"))).ShouldHaveSingleItem();

        result.TargetPath.ShouldBe(Path.Combine(xdgConfigHome, "opencode", "skills", "xdg-skill"));
        File.Exists(Path.Combine(result.TargetPath!, "SKILL.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task SyncSkillAsync_UsesLocalPathAsTheSource()
    {
        var localSkill = Path.Combine(_testDir, "my-skills", "local-skill");
        Directory.CreateDirectory(localSkill);
        await File.WriteAllTextAsync(Path.Combine(localSkill, "SKILL.md"), "# local");

        var result = (await _syncEngine.SyncSkillAsync(
            Entry("local-skill") with { Source = SkillSource.Local, LocalPath = localSkill })).ShouldHaveSingleItem();

        result.Success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(_openCodeSkillsDir, "local-skill", "SKILL.md")).ShouldBe("# local");
    }

    [Fact]
    public async Task SyncSkillAsync_CopiesASkillForOpenCodeToASeparateOpenCode2sConfigFolderToo()
    {
        CreateSource("both-skill");
        var openCode2Skills = Path.Combine(_testDir, ".weave", "harnesses", "opencode2", "config", "skills");
        Directory.CreateDirectory(Path.GetDirectoryName(openCode2Skills)!);

        var results = await _syncEngine.SyncSkillAsync(Entry("both-skill", harnesses: ["opencode"]));

        results.Select(r => (r.Harness, r.Success, r.TargetPath)).ShouldBe(
        [
            ("opencode", true, Path.Combine(_openCodeSkillsDir, "both-skill")),
            ("opencode2", true, Path.Combine(openCode2Skills, "both-skill")),
        ]);
        File.Exists(Path.Combine(openCode2Skills, "both-skill", "SKILL.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task SyncSkillAsync_ForProject_CopiesOnceIntoTheOpenCodeFolderBothOpenCodesRead()
    {
        CreateSource("repo-both");
        Directory.CreateDirectory(Path.Combine(_testDir, ".weave", "harnesses", "opencode2", "config"));

        var results = await _syncEngine.SyncSkillAsync(
            Entry("repo-both", harnesses: ["opencode"]) with { Scope = InstallScope.Project, ProjectPath = _repoDir });

        var result = results.ShouldHaveSingleItem();
        result.Harness.ShouldBe("opencode");
        result.TargetPath.ShouldBe(Path.Combine(_repoDir, ".opencode", "skills", "repo-both"));
    }

    [Fact]
    public async Task SyncHarnessAsync_CopiesEverySkillForThatHarness_AndRecordsWhereTheyLanded()
    {
        CreateSource("older-skill");
        CreateSource("claude-only");
        await _manifestStore.AddEntryAsync("local-user", null, Entry("older-skill", harnesses: ["opencode"]));
        await _manifestStore.AddEntryAsync("local-user", null, Entry("claude-only", harnesses: ["claude-code"]));
        var openCode2Skills = Path.Combine(_testDir, ".weave", "harnesses", "opencode2", "config", "skills");
        Directory.CreateDirectory(Path.GetDirectoryName(openCode2Skills)!);

        var result = (await _syncEngine.SyncHarnessAsync("opencode2")).ShouldHaveSingleItem();

        result.SkillName.ShouldBe("older-skill");
        result.TargetPath.ShouldBe(Path.Combine(openCode2Skills, "older-skill"));
        Directory.Exists(Path.Combine(_openCodeSkillsDir, "older-skill")).ShouldBeFalse();
        var manifest = await _manifestStore.LoadAsync("local-user");
        manifest.Skills.First(s => s.Name == "older-skill").InstalledPaths.ShouldBe([Path.Combine(openCode2Skills, "older-skill")]);
        _poolRecycler.RecycleCount.ShouldBe(0);
    }

    [Fact]
    public async Task SyncSkillAsync_SyncsToMultipleHarnesses()
    {
        CreateSource("multi-harness-skill");

        var results = await _syncEngine.SyncSkillAsync(Entry("multi-harness-skill", harnesses: ["opencode", "claude-code"]));

        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => r.Success);
        results.First(r => r.Harness == "opencode").TargetPath.ShouldBe(Path.Combine(_openCodeSkillsDir, "multi-harness-skill"));
        results.First(r => r.Harness == "claude-code").TargetPath.ShouldBe(Path.Combine(_claudeCodeSkillsDir, "multi-harness-skill"));
        _poolRecycler.RecycleCount.ShouldBe(1);
    }

    [Fact]
    public async Task SyncSkillAsync_WhenSourceDirectoryDoesNotExist_ReturnsErrorForAllHarnesses()
    {
        var results = await _syncEngine.SyncSkillAsync(Entry("missing-skill", harnesses: ["opencode", "claude-code"]));

        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => !r.Success && !r.Skipped);
        results.ShouldAllBe(r => r.ErrorMessage!.Contains("Source directory not found"));
        _poolRecycler.RecycleCount.ShouldBe(0);
    }

    [Fact]
    public async Task SyncSkillAsync_WithUnknownHarness_ReturnsError()
    {
        CreateSource("unknown-harness-skill");

        var result = (await _syncEngine.SyncSkillAsync(Entry("unknown-harness-skill", harnesses: ["unknown-harness"])))
            .ShouldHaveSingleItem();

        result.Success.ShouldBeFalse();
        result.Skipped.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("Unknown harness");
        _poolRecycler.RecycleCount.ShouldBe(0);
    }

    [Fact]
    public async Task SyncSkillAsync_WithEmptySkillName_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(() => _syncEngine.SyncSkillAsync(Entry("")));
        await Should.ThrowAsync<ArgumentException>(() => _syncEngine.SyncSkillAsync(Entry("   ")));
    }

    // ── SyncSkillAsync: folders already there ─────────────────────────────────

    [Fact]
    public async Task SyncSkillAsync_WhenAFolderFleetDidNotWriteIsThere_SkipsAndLeavesItAlone()
    {
        CreateSource("user-managed-skill");
        var targetPath = Path.Combine(_openCodeSkillsDir, "user-managed-skill");
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(targetPath, "user-file.txt"), "User content");

        var result = (await _syncEngine.SyncSkillAsync(Entry("user-managed-skill"))).ShouldHaveSingleItem();

        result.Success.ShouldBeFalse();
        result.Skipped.ShouldBeTrue();
        result.ErrorMessage!.ShouldContain("didn't install");
        File.Exists(Path.Combine(targetPath, "user-file.txt")).ShouldBeTrue();
        _poolRecycler.RecycleCount.ShouldBe(0);
    }

    [Fact]
    public async Task SyncSkillAsync_WhenTheSameFilesAreAlreadyThere_TakesThemOver()
    {
        var sourceDir = CreateSource("same-skill");
        InstalledFiles.CopyDirectory(sourceDir, Path.Combine(_openCodeSkillsDir, "same-skill"));

        var result = (await _syncEngine.SyncSkillAsync(Entry("same-skill"))).ShouldHaveSingleItem();

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task SyncSkillAsync_WhenFleetRecordedTheFolder_ReplacesIt()
    {
        CreateSource("recorded-skill", "# Updated");
        var targetPath = Path.Combine(_openCodeSkillsDir, "recorded-skill");
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(targetPath, "old-file.txt"), "Old content");

        var result = (await _syncEngine.SyncSkillAsync(Entry("recorded-skill") with { InstalledPaths = [targetPath] }))
            .ShouldHaveSingleItem();

        result.Success.ShouldBeTrue();
        File.Exists(Path.Combine(targetPath, "old-file.txt")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(targetPath, "SKILL.md")).ShouldBe("# Updated");
    }

    [Fact]
    public async Task SyncSkillAsync_WhenAnOlderFleetMarkedTheFolder_ReplacesIt()
    {
        CreateSource("fleet-managed-skill", "# Updated");
        var targetPath = Path.Combine(_openCodeSkillsDir, "fleet-managed-skill");
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(targetPath, InstalledFiles.LegacyManagedMarker), "Managed by Weave Fleet");
        await File.WriteAllTextAsync(Path.Combine(targetPath, "old-file.txt"), "Old content");

        var result = (await _syncEngine.SyncSkillAsync(Entry("fleet-managed-skill"))).ShouldHaveSingleItem();

        result.Success.ShouldBeTrue();
        File.Exists(Path.Combine(targetPath, "old-file.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(targetPath, InstalledFiles.LegacyManagedMarker)).ShouldBeFalse();
        File.ReadAllText(Path.Combine(targetPath, "SKILL.md")).ShouldBe("# Updated");
    }

    [Fact]
    public async Task SyncSkillAsync_WhenAnOlderFleetLinkedTheFolder_ReplacesTheLinkWithACopy()
    {
        var sourceDir = CreateSource("linked-skill");
        var targetPath = Path.Combine(_openCodeSkillsDir, "linked-skill");
        if (!TryCreateLink(targetPath, sourceDir))
            return;

        var result = (await _syncEngine.SyncSkillAsync(Entry("linked-skill"))).ShouldHaveSingleItem();

        result.Success.ShouldBeTrue();
        new DirectoryInfo(targetPath).LinkTarget.ShouldBeNull();
        File.Exists(Path.Combine(targetPath, "SKILL.md")).ShouldBeTrue();
        File.Exists(Path.Combine(sourceDir, "SKILL.md")).ShouldBeTrue("the cache the link pointed at stays");
    }

    [Fact]
    public async Task SyncSkillAsync_WhenTheFolderLinksSomewhereElse_SkipsIt()
    {
        CreateSource("other-link-skill");
        var usersSkill = Path.Combine(_testDir, "elsewhere", "other-link-skill");
        Directory.CreateDirectory(usersSkill);
        await File.WriteAllTextAsync(Path.Combine(usersSkill, "SKILL.md"), "# mine");
        var targetPath = Path.Combine(_openCodeSkillsDir, "other-link-skill");
        if (!TryCreateLink(targetPath, usersSkill))
            return;

        var result = (await _syncEngine.SyncSkillAsync(Entry("other-link-skill"))).ShouldHaveSingleItem();

        result.Skipped.ShouldBeTrue();
        new DirectoryInfo(targetPath).LinkTarget.ShouldNotBeNull();
        File.ReadAllText(Path.Combine(usersSkill, "SKILL.md")).ShouldBe("# mine");
    }

    [Fact]
    public async Task SyncSkillAsync_WhenTheLocalSourceIsTheHarnessFolder_SkipsWithoutDeletingIt()
    {
        var inPlace = Path.Combine(_openCodeSkillsDir, "in-place-skill");
        Directory.CreateDirectory(inPlace);
        await File.WriteAllTextAsync(Path.Combine(inPlace, "SKILL.md"), "# in place");

        var result = (await _syncEngine.SyncSkillAsync(
            Entry("in-place-skill") with { Source = SkillSource.Local, LocalPath = inPlace })).ShouldHaveSingleItem();

        result.Skipped.ShouldBeTrue();
        File.ReadAllText(Path.Combine(inPlace, "SKILL.md")).ShouldBe("# in place");
    }

    // ── RemoveSkillAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveSkillAsync_DeletesTheFoldersFleetInstalled()
    {
        CreateSource("removable-skill");
        var entry = Entry("removable-skill", harnesses: ["opencode", "claude-code"]);
        var synced = entry.WithSyncedPaths(await _syncEngine.SyncSkillAsync(entry));

        var results = await _syncEngine.RemoveSkillAsync(synced);

        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => r.Success);
        Directory.Exists(Path.Combine(_openCodeSkillsDir, "removable-skill")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_claudeCodeSkillsDir, "removable-skill")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_weaveSkillsDir, "removable-skill")).ShouldBeTrue("the source stays");
    }

    [Fact]
    public async Task RemoveSkillAsync_ForProject_DeletesOnlyTheRepositoryCopy()
    {
        CreateSource("both-skill");
        var global = Entry("both-skill");
        var project = global with { Scope = InstallScope.Project, ProjectPath = _repoDir };
        await _syncEngine.SyncSkillAsync(global);
        var syncedProject = project.WithSyncedPaths(await _syncEngine.SyncSkillAsync(project));

        await _syncEngine.RemoveSkillAsync(syncedProject);

        Directory.Exists(Path.Combine(_repoDir, ".opencode", "skills", "both-skill")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_openCodeSkillsDir, "both-skill")).ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveSkillAsync_LeavesFoldersFleetDidNotWrite()
    {
        var targetPath = Path.Combine(_openCodeSkillsDir, "users-skill");
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(targetPath, "SKILL.md"), "# mine");

        var results = await _syncEngine.RemoveSkillAsync(Entry("users-skill"));

        results.ShouldBeEmpty();
        File.Exists(Path.Combine(targetPath, "SKILL.md")).ShouldBeTrue();
        _poolRecycler.RecycleCount.ShouldBe(0);
    }

    [Fact]
    public async Task RemoveSkillAsync_RemovesAnOlderFleetsLinkButNotWhatItPointsAt()
    {
        var sourceDir = CreateSource("legacy-link-skill");
        var targetPath = Path.Combine(_openCodeSkillsDir, "legacy-link-skill");
        if (!TryCreateLink(targetPath, sourceDir))
            return;

        var result = (await _syncEngine.RemoveSkillAsync(Entry("legacy-link-skill"))).ShouldHaveSingleItem();

        result.Success.ShouldBeTrue();
        InstalledFiles.Exists(targetPath).ShouldBeFalse();
        File.Exists(Path.Combine(sourceDir, "SKILL.md")).ShouldBeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string CreateSource(string name, string content = "")
    {
        var dir = Path.Combine(_weaveSkillsDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content.Length > 0 ? content : $"# {name}");
        return dir;
    }

    private static SkillManifestEntry Entry(string name, IReadOnlyList<string>? harnesses = null) => new()
    {
        Name = name,
        Source = SkillSource.GitHub,
        RepoUrl = "https://github.com/test/skill",
        Ref = "main",
        TargetHarnesses = harnesses ?? ["opencode"],
        InstalledAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>Directory links need a privilege on Windows; tests that need one skip without it.</summary>
    private static bool TryCreateLink(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ── Test doubles ───────────────────────────────────────────────────────

    private sealed class FakeHarnessPoolRecycler : IHarnessPoolRecycler
    {
        public int RecycleCount { get; private set; }

        public Task<int> RecycleIdleInstancesAsync(CancellationToken cancellationToken = default)
        {
            RecycleCount++;
            return Task.FromResult(0);
        }
    }
}

// ── Collection definition to ensure tests run sequentially ────────────────

[CollectionDefinition("SkillTests", DisableParallelization = true)]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public class SkillTestsCollection
#pragma warning restore CA1711
{
}
