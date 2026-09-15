using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Domain.Tools;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Skills;
using WeaveFleet.Infrastructure.Tools;

namespace WeaveFleet.Infrastructure.Tests.Tools;

/// <summary>
/// Installs made by older Fleet versions, recreated on disk the way they left them.
/// </summary>
public sealed class LegacyInstallMigrationTests : IDisposable
{
    private const string ToolSource = "export default {}\n";

    private readonly string _home;
    private readonly string _openCodeDir;
    private readonly JsonSkillManifestStore _skillStore;
    private readonly JsonToolManifestStore _toolStore;
    private readonly LegacyInstallMigrationHostedService _migration;

    public LegacyInstallMigrationTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"weave-migrate-{Guid.NewGuid():N}");
        _openCodeDir = Path.Combine(_home, ".config", "opencode");
        Directory.CreateDirectory(_home);

        var paths = new HarnessInstallPaths(_home);
        _skillStore = new JsonSkillManifestStore(_home);
        _toolStore = new JsonToolManifestStore(_home);
        _migration = new LegacyInstallMigrationHostedService(
            _skillStore,
            new SkillSyncEngine(_skillStore, paths, NullLogger<SkillSyncEngine>.Instance),
            _toolStore,
            new ToolInstaller(paths, NullLogger<ToolInstaller>.Instance),
            paths,
            new FleetOptions(),
            NullLogger<LegacyInstallMigrationHostedService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    [Fact]
    public async Task NativeToolCopiedToTheOldFleetFolder_MovesIntoOpenCodesToolsFolder()
    {
        WriteFile(Path.Combine(_home, ".weave", "skills", "tool-lint", "lint.ts"), ToolSource);
        var legacyCopy = WriteFile(
            Path.Combine(_home, ".config", "weave-fleet", "tools", "local-user", "tool-lint", "lint.ts"), ToolSource);
        await _toolStore.AddEntryAsync("local-user", null, NativeTool("lint"));

        await _migration.StartAsync(CancellationToken.None);

        var installed = Path.Combine(_openCodeDir, "tools", "lint.ts");
        File.ReadAllText(installed).ShouldBe(ToolSource);
        (await _toolStore.LoadAsync("local-user")).Tools.ShouldHaveSingleItem().InstalledPath.ShouldBe(installed);
        File.Exists(legacyCopy).ShouldBeFalse();
        Directory.Exists(Path.Combine(_home, ".config", "weave-fleet", "tools")).ShouldBeFalse("the emptied legacy folders go too");
    }

    [Fact]
    public async Task NativeTool_WhenADifferentFileIsAlreadyInOpenCode_IsLeftForTheUser()
    {
        WriteFile(Path.Combine(_home, ".weave", "skills", "tool-lint", "lint.ts"), ToolSource);
        var handCopied = WriteFile(Path.Combine(_openCodeDir, "tools", "lint.ts"), "// copied by hand, one byte off");
        await _toolStore.AddEntryAsync("local-user", null, NativeTool("lint"));

        await _migration.StartAsync(CancellationToken.None);

        File.ReadAllText(handCopied).ShouldBe("// copied by hand, one byte off");
        (await _toolStore.LoadAsync("local-user")).Tools.ShouldHaveSingleItem().InstalledPath.ShouldBeNull();
    }

    [Fact]
    public async Task McpServerUnderTheLegacyKey_MovesToOpenCodesMcpSection()
    {
        var configPath = WriteFile(Path.Combine(_openCodeDir, "opencode.json"), """
            {
              // mine
              "mcpServers": {
                "fs": { "command": "npx", "args": ["-y", "server"] }
              }
            }
            """);
        await _toolStore.AddEntryAsync("local-user", null, new ToolManifestEntry
        {
            Name = "fs",
            ToolType = ToolType.Mcp,
            Source = SkillSource.Local,
            Command = "npx",
            Args = ["-y", "server"],
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await _migration.StartAsync(CancellationToken.None);

        var text = File.ReadAllText(configPath);
        text.ShouldContain("// mine");
        text.ShouldNotContain("mcpServers");
        var config = JsonNode.Parse(text, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;
        config["mcp"]!["fs"]!["command"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["npx", "-y", "server"]);
        (await _toolStore.LoadAsync("local-user")).Tools.ShouldHaveSingleItem().InstalledPath.ShouldBe(configPath);
    }

    [Fact]
    public async Task SkillLinkedIntoTheGlobalFolder_BecomesACopy()
    {
        var cache = Path.Combine(_home, ".weave", "skills", "review");
        WriteFile(Path.Combine(cache, "SKILL.md"), "# review");
        var target = Path.Combine(_openCodeDir, "skills", "review");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            Directory.CreateSymbolicLink(target, cache);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            return; // Directory links need a privilege on Windows.
        }

        await _skillStore.AddEntryAsync("local-user", null, new SkillManifestEntry
        {
            Name = "review",
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/pgermishuys/weave-fleet",
            LocalPath = cache,
            TargetHarnesses = ["opencode"],
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await _migration.StartAsync(CancellationToken.None);

        new DirectoryInfo(target).LinkTarget.ShouldBeNull();
        File.ReadAllText(Path.Combine(target, "SKILL.md")).ShouldBe("# review");
        (await _skillStore.LoadAsync("local-user")).Skills.ShouldHaveSingleItem().InstalledPaths.ShouldBe([target]);
    }

    [Fact]
    public async Task CatalogFleetApiSkill_IsRemoved_NowThatFleetShipsIt()
    {
        var installed = Path.Combine(_openCodeDir, "skills", "fleet-api");
        WriteFile(Path.Combine(installed, "SKILL.md"), "# fleet-api");
        WriteFile(Path.Combine(installed, "scripts", "fleet-api.sh"), "#!/usr/bin/env bash");
        await _skillStore.AddEntryAsync("local-user", null, CatalogSkill("fleet-api", "https://github.com/pgermishuys/weave-fleet") with
        {
            InstalledPaths = [installed]
        });

        await _migration.StartAsync(CancellationToken.None);

        Directory.Exists(installed).ShouldBeFalse();
        (await _skillStore.LoadAsync("local-user")).Skills.ShouldBeEmpty();
    }

    [Fact]
    public async Task FleetApiSkillFromAnotherRepository_IsKept()
    {
        var installed = Path.Combine(_openCodeDir, "skills", "fleet-api");
        WriteFile(Path.Combine(installed, "SKILL.md"), "# someone else's fleet-api");
        await _skillStore.AddEntryAsync("local-user", null, CatalogSkill("fleet-api", "https://github.com/someone/skills") with
        {
            InstalledPaths = [installed]
        });

        await _migration.StartAsync(CancellationToken.None);

        File.ReadAllText(Path.Combine(installed, "SKILL.md")).ShouldBe("# someone else's fleet-api");
        (await _skillStore.LoadAsync("local-user")).Skills.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CatalogVisualizeTool_IsRemoved_NowThatTheCanvasToolsReplaceIt()
    {
        var installed = WriteFile(Path.Combine(_openCodeDir, "tools", "visualize.ts"), ToolSource);
        await _toolStore.AddEntryAsync("local-user", null, NativeTool("visualize") with { InstalledPath = installed });

        await _migration.StartAsync(CancellationToken.None);

        File.Exists(installed).ShouldBeFalse();
        (await _toolStore.LoadAsync("local-user")).Tools.ShouldBeEmpty();
    }

    [Fact]
    public async Task CatalogVisualizeTool_InTheOldFleetFolder_IsRemovedFromEverywhere()
    {
        WriteFile(Path.Combine(_home, ".weave", "skills", "tool-visualize", "visualize.ts"), ToolSource);
        WriteFile(Path.Combine(_home, ".config", "weave-fleet", "tools", "local-user", "tool-visualize", "visualize.ts"), ToolSource);
        await _toolStore.AddEntryAsync("local-user", null, NativeTool("visualize"));

        await _migration.StartAsync(CancellationToken.None);

        File.Exists(Path.Combine(_openCodeDir, "tools", "visualize.ts")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_home, ".config", "weave-fleet", "tools")).ShouldBeFalse();
        (await _toolStore.LoadAsync("local-user")).Tools.ShouldBeEmpty();
    }

    [Fact]
    public async Task CatalogVisualizeTool_CopiedByHand_KeepsTheFile()
    {
        WriteFile(Path.Combine(_home, ".weave", "skills", "tool-visualize", "visualize.ts"), ToolSource);
        var handCopied = WriteFile(Path.Combine(_openCodeDir, "tools", "visualize.ts"), "// copied by hand, one byte off");
        await _toolStore.AddEntryAsync("local-user", null, NativeTool("visualize"));

        await _migration.StartAsync(CancellationToken.None);

        File.ReadAllText(handCopied).ShouldBe("// copied by hand, one byte off");
        (await _toolStore.LoadAsync("local-user")).Tools.ShouldBeEmpty();
    }

    [Fact]
    public async Task InAuthMode_DoesNothing()
    {
        WriteFile(Path.Combine(_home, ".weave", "skills", "tool-lint", "lint.ts"), ToolSource);
        await _toolStore.AddEntryAsync("local-user", null, NativeTool("lint"));
        var paths = new HarnessInstallPaths(_home);
        var migration = new LegacyInstallMigrationHostedService(
            _skillStore,
            new SkillSyncEngine(_skillStore, paths, NullLogger<SkillSyncEngine>.Instance),
            _toolStore,
            new ToolInstaller(paths, NullLogger<ToolInstaller>.Instance),
            paths,
            new FleetOptions { Auth = { Enabled = true } },
            NullLogger<LegacyInstallMigrationHostedService>.Instance);

        await migration.StartAsync(CancellationToken.None);

        File.Exists(Path.Combine(_openCodeDir, "tools", "lint.ts")).ShouldBeFalse();
    }

    private static SkillManifestEntry CatalogSkill(string name, string repoUrl) => new()
    {
        Name = name,
        Source = SkillSource.GitHub,
        RepoUrl = repoUrl,
        TargetHarnesses = ["opencode"],
        InstalledAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static ToolManifestEntry NativeTool(string name) => new()
    {
        Name = name,
        ToolType = ToolType.Native,
        Source = SkillSource.GitHub,
        RepoUrl = "https://github.com/pgermishuys/weave-fleet",
        InstalledAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
