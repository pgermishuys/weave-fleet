using WeaveFleet.Domain.Skills;
using WeaveFleet.Infrastructure.Skills;

namespace WeaveFleet.Infrastructure.Tests.Skills;

[Collection("SkillTests")]
public sealed class JsonSkillManifestStoreTests : IDisposable
{
    private readonly string _testDir;
    private readonly JsonSkillManifestStore _store;

    public JsonSkillManifestStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"weave-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        
        _store = new JsonSkillManifestStore(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsEmptyManifest()
    {
        // Act
        var manifest = await _store.LoadAsync("test-user");

        // Assert
        Assert.NotNull(manifest);
        Assert.Equal("test-user", manifest.UserId);
        Assert.Null(manifest.WorkspaceId);
        Assert.Empty(manifest.Skills);
        Assert.Equal("manifest-test-user", manifest.Id);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsManifest()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var manifest = new SkillManifest
        {
            Id = "manifest-test-user",
            UserId = "test-user",
            WorkspaceId = null,
            Skills = new[]
            {
                new SkillManifestEntry
                {
                    Name = "test-skill",
                    Source = SkillSource.GitHub,
                    RepoUrl = "https://github.com/test/skill",
                    Ref = "main",
                    LocalPath = null,
                    TargetHarnesses = new[] { "opencode", "aider" },
                    InstalledAt = now,
                    UpdatedAt = now
                }
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        // Act
        await _store.SaveAsync(manifest);
        var loaded = await _store.LoadAsync("test-user");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(manifest.Id, loaded.Id);
        Assert.Equal(manifest.UserId, loaded.UserId);
        Assert.Equal(manifest.WorkspaceId, loaded.WorkspaceId);
        Assert.Single(loaded.Skills);
        
        var skill = loaded.Skills[0];
        Assert.Equal("test-skill", skill.Name);
        Assert.Equal(SkillSource.GitHub, skill.Source);
        Assert.Equal("https://github.com/test/skill", skill.RepoUrl);
        Assert.Equal("main", skill.Ref);
        Assert.Null(skill.LocalPath);
        Assert.Equal(2, skill.TargetHarnesses.Count);
        Assert.Contains("opencode", skill.TargetHarnesses);
        Assert.Contains("aider", skill.TargetHarnesses);
    }

    [Fact]
    public async Task SaveAsync_WithAllSkillSourceTypes_RoundTripsCorrectly()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var manifest = new SkillManifest
        {
            Id = "manifest-test-user",
            UserId = "test-user",
            WorkspaceId = null,
            Skills = new[]
            {
                new SkillManifestEntry
                {
                    Name = "bundled-skill",
                    Source = SkillSource.Bundled,
                    RepoUrl = null,
                    Ref = null,
                    LocalPath = null,
                    TargetHarnesses = new[] { "opencode" },
                    InstalledAt = now,
                    UpdatedAt = now
                },
                new SkillManifestEntry
                {
                    Name = "github-skill",
                    Source = SkillSource.GitHub,
                    RepoUrl = "https://github.com/test/skill",
                    Ref = "v1.0.0",
                    LocalPath = null,
                    TargetHarnesses = new[] { "opencode", "aider" },
                    InstalledAt = now,
                    UpdatedAt = now
                },
                new SkillManifestEntry
                {
                    Name = "local-skill",
                    Source = SkillSource.Local,
                    RepoUrl = null,
                    Ref = null,
                    LocalPath = "/path/to/skill",
                    TargetHarnesses = new[] { "opencode" },
                    InstalledAt = now,
                    UpdatedAt = now
                }
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        // Act
        await _store.SaveAsync(manifest);
        var loaded = await _store.LoadAsync("test-user");

        // Assert
        Assert.Equal(3, loaded.Skills.Count);
        
        var bundled = loaded.Skills.First(s => s.Name == "bundled-skill");
        Assert.Equal(SkillSource.Bundled, bundled.Source);
        
        var github = loaded.Skills.First(s => s.Name == "github-skill");
        Assert.Equal(SkillSource.GitHub, github.Source);
        Assert.Equal("https://github.com/test/skill", github.RepoUrl);
        Assert.Equal("v1.0.0", github.Ref);
        
        var local = loaded.Skills.First(s => s.Name == "local-skill");
        Assert.Equal(SkillSource.Local, local.Source);
        Assert.Equal("/path/to/skill", local.LocalPath);
    }

    [Fact]
    public async Task SaveAsync_WritesAtomically()
    {
        // Arrange
        var manifest = new SkillManifest
        {
            Id = "manifest-test-user",
            UserId = "test-user",
            WorkspaceId = null,
            Skills = [],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Act
        await _store.SaveAsync(manifest);

        // Assert - verify no .tmp file exists after save
        var skillsDir = Path.Combine(_testDir, ".weave", "skills");
        var tmpFiles = Directory.GetFiles(skillsDir, "*.tmp");
        Assert.Empty(tmpFiles);
        
        // Verify the actual file exists
        var manifestFile = Path.Combine(skillsDir, "test-user.json");
        Assert.True(File.Exists(manifestFile));
    }

    [Fact]
    public async Task AddEntryAsync_AddsNewSkill()
    {
        // Arrange
        var entry = new SkillManifestEntry
        {
            Name = "new-skill",
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/new-skill",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Act
        await _store.AddEntryAsync("test-user", null, entry);
        var manifest = await _store.LoadAsync("test-user");

        // Assert
        Assert.Single(manifest.Skills);
        Assert.Equal("new-skill", manifest.Skills[0].Name);
    }

    [Fact]
    public async Task AddEntryAsync_WhenSkillExists_ThrowsException()
    {
        // Arrange
        var entry = new SkillManifestEntry
        {
            Name = "duplicate-skill",
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _store.AddEntryAsync("test-user", null, entry);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.AddEntryAsync("test-user", null, entry));
    }

    [Fact]
    public async Task RemoveEntryAsync_RemovesExistingSkill()
    {
        // Arrange
        var entry = new SkillManifestEntry
        {
            Name = "skill-to-remove",
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _store.AddEntryAsync("test-user", null, entry);

        // Act
        await _store.RemoveEntryAsync("test-user", null, "skill-to-remove");
        var manifest = await _store.LoadAsync("test-user");

        // Assert
        Assert.Empty(manifest.Skills);
    }

    [Fact]
    public async Task RemoveEntryAsync_WhenSkillNotFound_ThrowsException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.RemoveEntryAsync("test-user", null, "non-existent-skill"));
    }

    [Fact]
    public async Task UpdateEntryAsync_UpdatesExistingSkill()
    {
        // Arrange
        var originalEntry = new SkillManifestEntry
        {
            Name = "skill-to-update",
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _store.AddEntryAsync("test-user", null, originalEntry);

        var updatedEntry = originalEntry with
        {
            Ref = "v2.0.0",
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1)
        };

        // Act
        await _store.UpdateEntryAsync("test-user", null, updatedEntry);
        var manifest = await _store.LoadAsync("test-user");

        // Assert
        Assert.Single(manifest.Skills);
        Assert.Equal("v2.0.0", manifest.Skills[0].Ref);
    }

    [Fact]
    public async Task UpdateEntryAsync_WhenSkillNotFound_ThrowsException()
    {
        // Arrange
        var entry = new SkillManifestEntry
        {
            Name = "non-existent-skill",
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/test/skill",
            Ref = "main",
            LocalPath = null,
            TargetHarnesses = new[] { "opencode" },
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.UpdateEntryAsync("test-user", null, entry));
    }

    [Fact]
    public async Task LoadAsync_WithWorkspaceId_UsesCorrectFilePath()
    {
        // Arrange
        var manifest = new SkillManifest
        {
            Id = "manifest-test-user-workspace1",
            UserId = "test-user",
            WorkspaceId = "workspace1",
            Skills = [],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        // Act
        await _store.SaveAsync(manifest);
        var loaded = await _store.LoadAsync("test-user", "workspace1");

        // Assert
        Assert.Equal("workspace1", loaded.WorkspaceId);
        
        // Verify the file path includes workspace ID
        var skillsDir = Path.Combine(_testDir, ".weave", "skills");
        var manifestFile = Path.Combine(skillsDir, "test-user_workspace1.json");
        Assert.True(File.Exists(manifestFile));
    }

    [Fact]
    public async Task ValidateUserId_WithInvalidCharacters_ThrowsException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.LoadAsync("user/with/slash"));
        
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.LoadAsync("user\\with\\backslash"));
        
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.LoadAsync("."));
        
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.LoadAsync(".."));
    }

    [Fact]
    public async Task ValidateWorkspaceId_WithInvalidCharacters_ThrowsException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.LoadAsync("test-user", "workspace/with/slash"));
        
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.LoadAsync("test-user", "workspace\\with\\backslash"));
        
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.LoadAsync("test-user", "."));
        
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.LoadAsync("test-user", ".."));
    }
}
