using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Domain.Tools;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Tools;

namespace WeaveFleet.Infrastructure.Tests.Tools;

public sealed class ToolInstallerTests : IDisposable
{
    private const string ToolSource = "import { tool } from \"@opencode-ai/plugin\"\nexport default tool({})\n";

    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly string _home;
    private readonly string _openCodeDir;
    private readonly string _repoDir;
    private readonly ToolInstaller _installer;

    public ToolInstallerTests()
    {
        _home = Path.Combine(Path.GetTempPath(), $"weave-tools-{Guid.NewGuid():N}");
        _openCodeDir = Path.Combine(_home, ".config", "opencode");
        _repoDir = Path.Combine(_home, "src", "my-repo");
        Directory.CreateDirectory(_repoDir);
        _installer = new ToolInstaller(new HarnessInstallPaths(_home), NullLogger<ToolInstaller>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    // ── Native tools ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallNative_Global_CopiesTheToolFileIntoOpenCodesToolsFolder()
    {
        var source = CreateSourceDir("sample-tool.ts", "other.ts");

        var result = await _installer.InstallNativeAsync("sample-tool", source, InstallTarget.Global);

        var expected = Path.Combine(_openCodeDir, "tools", "sample-tool.ts");
        result.Value.ShouldBe(expected);
        File.ReadAllText(expected).ShouldBe(ToolSource);
        File.Exists(Path.Combine(_openCodeDir, "tools", "other.ts")).ShouldBeFalse("only the named tool is installed");
    }

    [Fact]
    public async Task InstallNative_Project_CopiesIntoTheRepositorysOpenCodeFolder()
    {
        var source = CreateSourceDir("sample-tool.ts");

        var result = await _installer.InstallNativeAsync("sample-tool", source, InstallTarget.Project(_repoDir));

        result.Value.ShouldBe(Path.Combine(_repoDir, ".opencode", "tools", "sample-tool.ts"));
        File.Exists(result.Value).ShouldBeTrue();
        Directory.Exists(Path.Combine(_openCodeDir, "tools")).ShouldBeFalse();
    }

    [Fact]
    public async Task InstallNative_FromAFile_NamesItAfterTheTool()
    {
        var source = Path.Combine(CreateSourceDir("draw-thing.js"), "draw-thing.js");

        var result = await _installer.InstallNativeAsync("draw", source, InstallTarget.Global);

        result.Value.ShouldBe(Path.Combine(_openCodeDir, "tools", "draw.js"));
    }

    [Fact]
    public async Task InstallNative_FromAFolderWithOneToolFile_UsesIt()
    {
        var source = CreateSourceDir("index.ts", "README.md");

        var result = await _installer.InstallNativeAsync("my-tool", source, InstallTarget.Global);

        result.Value.ShouldBe(Path.Combine(_openCodeDir, "tools", "my-tool.ts"));
    }

    [Fact]
    public async Task InstallNative_FromAFolderWithNoMatchingFile_Fails()
    {
        var source = CreateSourceDir("a.ts", "b.ts");

        var result = await _installer.InstallNativeAsync("sample-tool", source, InstallTarget.Global);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldStartWith("Validation.");
    }

    [Fact]
    public async Task InstallNative_WhenADifferentFileIsThere_ConflictsAndLeavesItAlone()
    {
        var source = CreateSourceDir("sample-tool.ts");
        var existing = WriteFile(Path.Combine(_openCodeDir, "tools", "sample-tool.ts"), "// mine");

        var result = await _installer.InstallNativeAsync("sample-tool", source, InstallTarget.Global);

        result.Error.Code.ShouldBe(FleetError.Conflict.Code);
        File.ReadAllText(existing).ShouldBe("// mine");
    }

    [Fact]
    public async Task InstallNative_WhenTheJsTwinIsThere_Conflicts()
    {
        var source = CreateSourceDir("sample-tool.ts");
        WriteFile(Path.Combine(_openCodeDir, "tools", "sample-tool.js"), ToolSource);

        var result = await _installer.InstallNativeAsync("sample-tool", source, InstallTarget.Global);

        result.Error.Code.ShouldBe(FleetError.Conflict.Code);
    }

    [Fact]
    public async Task InstallNative_WhenTheSameFileIsThere_Succeeds()
    {
        var source = CreateSourceDir("sample-tool.ts");
        WriteFile(Path.Combine(_openCodeDir, "tools", "sample-tool.ts"), ToolSource);

        var result = await _installer.InstallNativeAsync("sample-tool", source, InstallTarget.Global);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task InstallNative_RespectsXdgConfigHome()
    {
        var xdg = Path.Combine(_home, "xdg");
        var installer = new ToolInstaller(new HarnessInstallPaths(_home, xdg), NullLogger<ToolInstaller>.Instance);

        var result = await installer.InstallNativeAsync("sample-tool", CreateSourceDir("sample-tool.ts"), InstallTarget.Global);

        result.Value.ShouldBe(Path.Combine(xdg, "opencode", "tools", "sample-tool.ts"));
    }

    [Fact]
    public async Task Uninstall_Native_DeletesTheFile()
    {
        var installed = (await _installer.InstallNativeAsync("sample-tool", CreateSourceDir("sample-tool.ts"), InstallTarget.Global)).Value;

        var result = await _installer.UninstallAsync(NativeEntry("sample-tool", installed));

        result.IsSuccess.ShouldBeTrue();
        File.Exists(installed).ShouldBeFalse();
    }

    [Fact]
    public async Task Uninstall_Native_RefusesAPathOutsideTheToolsFolder()
    {
        var elsewhere = WriteFile(Path.Combine(_home, "important.ts"), "keep");

        var result = await _installer.UninstallAsync(NativeEntry("sample-tool", elsewhere));

        result.IsFailure.ShouldBeTrue();
        File.Exists(elsewhere).ShouldBeTrue();
    }

    // ── MCP servers ───────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallMcp_Global_WritesOpenCodesShapeIntoANewConfig()
    {
        var result = await _installer.InstallMcpAsync(
            "filesystem", "npx", ["-y", "@modelcontextprotocol/server-filesystem"], new Dictionary<string, string> { ["DEBUG"] = "1" },
            InstallTarget.Global);

        var configPath = Path.Combine(_openCodeDir, "opencode.json");
        result.Value.ShouldBe(configPath);

        var config = JsonNode.Parse(File.ReadAllText(configPath))!;
        config["$schema"]!.GetValue<string>().ShouldBe("https://opencode.ai/config.json");
        var server = config["mcp"]!["filesystem"]!;
        server["type"]!.GetValue<string>().ShouldBe("local");
        server["command"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["npx", "-y", "@modelcontextprotocol/server-filesystem"]);
        server["environment"]!["DEBUG"]!.GetValue<string>().ShouldBe("1");
        server["enabled"]!.GetValue<bool>().ShouldBeTrue();
        config["mcpServers"].ShouldBeNull();
    }

    [Fact]
    public async Task InstallMcp_IntoAnExistingConfig_KeepsItsComments()
    {
        var configPath = WriteFile(Path.Combine(_openCodeDir, "opencode.json"), """
            {
              "provider": {
                // "baseURL": "http://127.0.0.1:5080"
              }
            }
            """);

        await _installer.InstallMcpAsync("fs", "npx", null, null, InstallTarget.Global);

        var text = File.ReadAllText(configPath);
        text.ShouldContain("// \"baseURL\": \"http://127.0.0.1:5080\"");
        JsonNode.Parse(text, documentOptions: Jsonc)!["mcp"]!["fs"].ShouldNotBeNull();
    }

    [Fact]
    public async Task InstallMcp_Project_WritesTheRepositoryConfig_PreferringAnExistingJsonc()
    {
        var jsonc = WriteFile(Path.Combine(_repoDir, "opencode.jsonc"), "{\n  // project\n}\n");

        var result = await _installer.InstallMcpAsync("fs", "npx", null, null, InstallTarget.Project(_repoDir));

        result.Value.ShouldBe(jsonc);
        File.Exists(Path.Combine(_repoDir, "opencode.json")).ShouldBeFalse();
        JsonNode.Parse(File.ReadAllText(jsonc), documentOptions: Jsonc)!["mcp"]!["fs"].ShouldNotBeNull();
    }

    [Fact]
    public async Task InstallMcp_WhenADifferentServerHasTheName_Conflicts()
    {
        var configPath = WriteFile(Path.Combine(_openCodeDir, "opencode.json"), """
            { "mcp": { "fs": { "type": "remote", "url": "https://example.test" } } }
            """);

        var result = await _installer.InstallMcpAsync("fs", "npx", null, null, InstallTarget.Global);

        result.Error.Code.ShouldBe(FleetError.Conflict.Code);
        File.ReadAllText(configPath).ShouldContain("https://example.test");
    }

    [Fact]
    public async Task Uninstall_Mcp_RemovesOnlyThatServer()
    {
        var configPath = (await _installer.InstallMcpAsync("fs", "npx", null, null, InstallTarget.Global)).Value;
        await _installer.InstallMcpAsync("other", "node", null, null, InstallTarget.Global);

        var result = await _installer.UninstallAsync(McpEntry("fs", configPath));

        result.IsSuccess.ShouldBeTrue();
        var mcp = JsonNode.Parse(File.ReadAllText(configPath))!["mcp"]!.AsObject();
        mcp.Select(p => p.Key).ShouldBe(["other"]);
    }

    [Fact]
    public async Task Uninstall_Mcp_FromAnOlderFleet_RemovesTheLegacyEntryAndEmptyKey()
    {
        var configPath = WriteFile(Path.Combine(_openCodeDir, "opencode.json"), """
            {
              "permission": "allow",
              "mcpServers": {
                "fs": { "command": "npx" }
              }
            }
            """);

        var result = await _installer.UninstallAsync(McpEntry("fs", installedPath: null));

        result.IsSuccess.ShouldBeTrue();
        File.ReadAllText(configPath).ShouldBe("""
            {
              "permission": "allow"
            }
            """);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string CreateSourceDir(params string[] files)
    {
        var dir = Path.Combine(_home, "source", Guid.NewGuid().ToString("N"));
        foreach (var file in files)
            WriteFile(Path.Combine(dir, file), ToolSource);
        return dir;
    }

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static ToolManifestEntry NativeEntry(string name, string? installedPath) => new()
    {
        Name = name,
        ToolType = ToolType.Native,
        Source = SkillSource.GitHub,
        InstalledPath = installedPath,
        InstalledAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static ToolManifestEntry McpEntry(string name, string? installedPath) => new()
    {
        Name = name,
        ToolType = ToolType.Mcp,
        Source = SkillSource.Local,
        Command = "npx",
        InstalledPath = installedPath,
        InstalledAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
