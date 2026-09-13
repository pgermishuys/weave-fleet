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
        WriteFile(Path.Combine(_home, ".weave", "skills", "tool-visualize", "visualize.ts"), ToolSource);
        var legacyCopy = WriteFile(
            Path.Combine(_home, ".config", "weave-fleet", "tools", "local-user", "tool-visualize", "visualize.ts"), ToolSource);
        await _toolStore.AddEntryAsync("local-user", null, NativeTool("visualize"));

        await _migration.StartAsync(CancellationToken.None);

        var installed = Path.Combine(_openCodeDir, "tools", "visualize.ts");
        File.ReadAllText(installed).ShouldBe(ToolSource);
        (await _toolStore.LoadAsync("local-user")).Tools.ShouldHaveSingleItem().InstalledPath.ShouldBe(installed);
        File.Exists(legacyCopy).ShouldBeFalse();
        Directory.Exists(Path.Combine(_home, ".config", "weave-fleet", "tools")).ShouldBeFalse("the emptied legacy folders go too");
    }

    [Fact]
    public async Task NativeTool_WhenADifferentFileIsAlreadyInOpenCode_IsLeftForTheUser()
    {
        WriteFile(Path.Combine(_home, ".weave", "skills", "tool-visualize", "visualize.ts"), ToolSource);
        var handCopied = WriteFile(Path.Combine(_openCodeDir, "tools", "visualize.ts"), "// copied by hand, one byte off");
        await _toolStore.AddEntryAsync("local-user", null, NativeTool("visualize"));

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
        var cache = Path.Combine(_home, ".weave", "skills", "fleet-api");
        WriteFile(Path.Combine(cache, "SKILL.md"), "# fleet-api");
        var target = Path.Combine(_openCodeDir, "skills", "fleet-api");
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
            Name = "fleet-api",
            Source = SkillSource.GitHub,
            RepoUrl = "https://github.com/pgermishuys/weave-fleet",
            LocalPath = cache,
            TargetHarnesses = ["opencode"],
            InstalledAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await _migration.StartAsync(CancellationToken.None);

        new DirectoryInfo(target).LinkTarget.ShouldBeNull();
        File.ReadAllText(Path.Combine(target, "SKILL.md")).ShouldBe("# fleet-api");
        (await _skillStore.LoadAsync("local-user")).Skills.ShouldHaveSingleItem().InstalledPaths.ShouldBe([target]);
    }

    [Fact]
    public async Task InAuthMode_DoesNothing()
    {
        WriteFile(Path.Combine(_home, ".weave", "skills", "tool-visualize", "visualize.ts"), ToolSource);
        await _toolStore.AddEntryAsync("local-user", null, NativeTool("visualize"));
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

        File.Exists(Path.Combine(_openCodeDir, "tools", "visualize.ts")).ShouldBeFalse();
    }

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
