using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Infrastructure.Skills;

namespace WeaveFleet.Infrastructure.Tests.Skills;

[Collection("SkillTests")]
public sealed class SkillSyncEngineTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _weaveSkillsDir;
    private readonly string _openCodeSkillsDir;
    private readonly string _claudeCodeSkillsDir;
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

        _manifestStore = new JsonSkillManifestStore(_testDir);
        _poolRecycler = new FakeHarnessPoolRecycler();
        _syncEngine = new SkillSyncEngine(_manifestStore, NullLogger<SkillSyncEngine>.Instance, _poolRecycler, _testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    [Fact]
    public async Task SyncAllAsync_WhenManifestIsEmpty_ReturnsEmptyResults()
    {
        // Act
        var results = await _syncEngine.SyncAllAsync();

        // Assert
        results.ShouldBeEmpty();
        _poolRecycler.RecycleCount.ShouldBe(0);
    }

    [Fact]
    public async Task SyncSkillAsync_WhenSkillNotInManifest_ReturnsErrorResult()
    {
        // Act
        var results = await _syncEngine.SyncSkillAsync("non-existent-skill");

        // Assert
        results.ShouldHaveSingleItem();
        var result = results[0];
        result.SkillName.ShouldBe("non-existent-skill");
        result.Success.ShouldBeFalse();
        result.Skipped.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("not found in manifest");
        _poolRecycler.RecycleCount.ShouldBe(0);
    }

    [Fact]
    public async Task SyncSkillAsync_WhenSourceDirectoryDoesNotExist_ReturnsErrorForAllHarnesses()
    {
        // Arrange
        var entry = new SkillManifestEntry
        {
            Name = "missing-skill",
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode", "claude-code" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _manifestStore.AddEntryAsync("local-user", null, entry);

        // Act
        var results = await _syncEngine.SyncSkillAsync("missing-skill");

        // Assert
        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => !r.Success);
        results.ShouldAllBe(r => !r.Skipped);
        results.ShouldAllBe(r => r.ErrorMessage!.Contains("Source directory not found"));
        _poolRecycler.RecycleCount.ShouldBe(0);
    }

    [Fact]
    public async Task SyncSkillAsync_CreatesSymlinkOnMacOsLinux_CopiesOnWindows()
    {
        // Arrange
        var skillName = "test-skill";
        var sourceDir = Path.Combine(_weaveSkillsDir, skillName);
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "SKILL.md"), "# Test Skill");

        var entry = new SkillManifestEntry
        {
            Name = skillName,
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _manifestStore.AddEntryAsync("local-user", null, entry);

        // Act
        var results = await _syncEngine.SyncSkillAsync(skillName);

        // Assert
        results.ShouldHaveSingleItem();
        var result = results[0];
        result.Success.ShouldBeTrue();
        result.Skipped.ShouldBeFalse();
        result.SkillName.ShouldBe(skillName);
        result.Harness.ShouldBe("opencode");

        var targetPath = Path.Combine(_openCodeSkillsDir, skillName);
        Directory.Exists(targetPath).ShouldBeTrue();

        // Verify marker file exists
        var markerPath = Path.Combine(targetPath, ".fleet-managed");
        File.Exists(markerPath).ShouldBeTrue();
        var markerContent = await File.ReadAllTextAsync(markerPath);
        markerContent.ShouldContain("Managed by Weave Fleet");
        markerContent.ShouldContain(sourceDir);

        // Verify skill file exists
        var skillFilePath = Path.Combine(targetPath, "SKILL.md");
        File.Exists(skillFilePath).ShouldBeTrue();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On Windows, should be a copy (not a symlink)
            var linkInfo = new DirectoryInfo(targetPath);
            linkInfo.LinkTarget.ShouldBeNull();
        }
        else
        {
            // On macOS/Linux, should be a symlink
            var linkInfo = new DirectoryInfo(targetPath);
            linkInfo.LinkTarget.ShouldBe(sourceDir);
        }

        _poolRecycler.RecycleCount.ShouldBe(1);
    }

    [Fact]
    public async Task SyncSkillAsync_WhenTargetExistsWithoutMarker_SkipsSync()
    {
        // Arrange
        var skillName = "user-managed-skill";
        var sourceDir = Path.Combine(_weaveSkillsDir, skillName);
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "SKILL.md"), "# Test Skill");

        // Create user-managed directory (no .fleet-managed marker)
        var targetPath = Path.Combine(_openCodeSkillsDir, skillName);
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(targetPath, "user-file.txt"), "User content");

        var entry = new SkillManifestEntry
        {
            Name = skillName,
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _manifestStore.AddEntryAsync("local-user", null, entry);

        // Act
        var results = await _syncEngine.SyncSkillAsync(skillName);

        // Assert
        results.ShouldHaveSingleItem();
        var result = results[0];
        result.Success.ShouldBeFalse();
        result.Skipped.ShouldBeTrue();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("not Fleet-managed");
        result.ErrorMessage.ShouldContain(".fleet-managed");

        // Verify user file still exists (not overwritten)
        File.Exists(Path.Combine(targetPath, "user-file.txt")).ShouldBeTrue();

        _poolRecycler.RecycleCount.ShouldBe(0);
    }

    [Fact]
    public async Task SyncSkillAsync_WhenTargetExistsWithMarker_ReplacesTarget()
    {
        // Arrange
        var skillName = "fleet-managed-skill";
        var sourceDir = Path.Combine(_weaveSkillsDir, skillName);
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "SKILL.md"), "# Updated Skill");

        // Create existing Fleet-managed directory
        var targetPath = Path.Combine(_openCodeSkillsDir, skillName);
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(targetPath, ".fleet-managed"), "Managed by Weave Fleet");
        await File.WriteAllTextAsync(Path.Combine(targetPath, "old-file.txt"), "Old content");

        var entry = new SkillManifestEntry
        {
            Name = skillName,
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _manifestStore.AddEntryAsync("local-user", null, entry);

        // Act
        var results = await _syncEngine.SyncSkillAsync(skillName);

        // Assert
        results.ShouldHaveSingleItem();
        var result = results[0];
        result.Success.ShouldBeTrue();
        result.Skipped.ShouldBeFalse();

        // Verify old file is gone
        File.Exists(Path.Combine(targetPath, "old-file.txt")).ShouldBeFalse();

        // Verify new file exists
        File.Exists(Path.Combine(targetPath, "SKILL.md")).ShouldBeTrue();

        _poolRecycler.RecycleCount.ShouldBe(1);
    }

    [Fact]
    public async Task SyncSkillAsync_SyncsToMultipleHarnesses()
    {
        // Arrange
        var skillName = "multi-harness-skill";
        var sourceDir = Path.Combine(_weaveSkillsDir, skillName);
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "SKILL.md"), "# Multi-Harness Skill");

        var entry = new SkillManifestEntry
        {
            Name = skillName,
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode", "claude-code" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _manifestStore.AddEntryAsync("local-user", null, entry);

        // Act
        var results = await _syncEngine.SyncSkillAsync(skillName);

        // Assert
        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => r.Success);
        results.ShouldAllBe(r => !r.Skipped);

        var openCodeResult = results.First(r => r.Harness == "opencode");
        openCodeResult.TargetPath.ShouldBe(Path.Combine(_openCodeSkillsDir, skillName));
        Directory.Exists(openCodeResult.TargetPath).ShouldBeTrue();

        var claudeCodeResult = results.First(r => r.Harness == "claude-code");
        claudeCodeResult.TargetPath.ShouldBe(Path.Combine(_claudeCodeSkillsDir, skillName));
        Directory.Exists(claudeCodeResult.TargetPath).ShouldBeTrue();

        _poolRecycler.RecycleCount.ShouldBe(1);
    }

    [Fact]
    public async Task SyncSkillAsync_WithUnknownHarness_ReturnsError()
    {
        // Arrange
        var skillName = "unknown-harness-skill";
        var sourceDir = Path.Combine(_weaveSkillsDir, skillName);
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "SKILL.md"), "# Test Skill");

        var entry = new SkillManifestEntry
        {
            Name = skillName,
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "unknown-harness" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _manifestStore.AddEntryAsync("local-user", null, entry);

        // Act
        var results = await _syncEngine.SyncSkillAsync(skillName);

        // Assert
        results.ShouldHaveSingleItem();
        var result = results[0];
        result.Success.ShouldBeFalse();
        result.Skipped.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Unknown harness");
        _poolRecycler.RecycleCount.ShouldBe(0);
    }

    [Fact]
    public async Task SyncAllAsync_SyncsAllSkillsInManifest()
    {
        // Arrange
        var skill1Name = "skill-1";
        var skill1Dir = Path.Combine(_weaveSkillsDir, skill1Name);
        Directory.CreateDirectory(skill1Dir);
        await File.WriteAllTextAsync(Path.Combine(skill1Dir, "SKILL.md"), "# Skill 1");

        var skill2Name = "skill-2";
        var skill2Dir = Path.Combine(_weaveSkillsDir, skill2Name);
        Directory.CreateDirectory(skill2Dir);
        await File.WriteAllTextAsync(Path.Combine(skill2Dir, "SKILL.md"), "# Skill 2");

        var entry1 = new SkillManifestEntry
        {
            Name = skill1Name,
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill1",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var entry2 = new SkillManifestEntry
        {
            Name = skill2Name,
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill2",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "claude-code" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _manifestStore.AddEntryAsync("local-user", null, entry1);
        await _manifestStore.AddEntryAsync("local-user", null, entry2);

        // Act
        var results = await _syncEngine.SyncAllAsync();

        // Assert
        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => r.Success);

        var skill1Result = results.First(r => r.SkillName == skill1Name);
        skill1Result.Harness.ShouldBe("opencode");

        var skill2Result = results.First(r => r.SkillName == skill2Name);
        skill2Result.Harness.ShouldBe("claude-code");

        _poolRecycler.RecycleCount.ShouldBe(1);
    }

    [Fact]
    public async Task SyncSkillAsync_WithEmptySkillName_ThrowsArgumentException()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(() => _syncEngine.SyncSkillAsync(""));
        await Should.ThrowAsync<ArgumentException>(() => _syncEngine.SyncSkillAsync("   "));
    }

    [Fact]
    public async Task SyncSkillAsync_OnWindows_CopiesDirectoryRecursively()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Skip on non-Windows platforms
            return;
        }

        // Arrange
        var skillName = "nested-skill";
        var sourceDir = Path.Combine(_weaveSkillsDir, skillName);
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "SKILL.md"), "# Root Skill");

        var subDir = Path.Combine(sourceDir, "subdir");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "nested.txt"), "Nested content");

        var entry = new SkillManifestEntry
        {
            Name = skillName,
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _manifestStore.AddEntryAsync("local-user", null, entry);

        // Act
        var results = await _syncEngine.SyncSkillAsync(skillName);

        // Assert
        results.ShouldHaveSingleItem();
        results[0].Success.ShouldBeTrue();

        var targetPath = Path.Combine(_openCodeSkillsDir, skillName);
        File.Exists(Path.Combine(targetPath, "SKILL.md")).ShouldBeTrue();
        File.Exists(Path.Combine(targetPath, "subdir", "nested.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task SyncAllAsync_WhenSomeSkillsFail_StillSyncsOthers()
    {
        // Arrange
        var goodSkillName = "good-skill";
        var goodSkillDir = Path.Combine(_weaveSkillsDir, goodSkillName);
        Directory.CreateDirectory(goodSkillDir);
        await File.WriteAllTextAsync(Path.Combine(goodSkillDir, "SKILL.md"), "# Good Skill");

        var badSkillName = "bad-skill";
        // Don't create directory for bad skill

        var goodEntry = new SkillManifestEntry
        {
            Name = goodSkillName,
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/good",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var badEntry = new SkillManifestEntry
        {
            Name = badSkillName,
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/bad",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _manifestStore.AddEntryAsync("local-user", null, goodEntry);
        await _manifestStore.AddEntryAsync("local-user", null, badEntry);

        // Act
        var results = await _syncEngine.SyncAllAsync();

        // Assert
        results.Count.ShouldBe(2);

        var goodResult = results.First(r => r.SkillName == goodSkillName);
        goodResult.Success.ShouldBeTrue();

        var badResult = results.First(r => r.SkillName == badSkillName);
        badResult.Success.ShouldBeFalse();
        badResult.ErrorMessage.ShouldNotBeNull();
        badResult.ErrorMessage.ShouldContain("Source directory not found");

        // Pool should still be recycled because at least one skill succeeded
        _poolRecycler.RecycleCount.ShouldBe(1);
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
