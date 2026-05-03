using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NuCode.Configuration;
using NuCode.Tools;

namespace NuCode;

public sealed class SkillToolTests : IDisposable
{
    private readonly string _tempDir;

    public SkillToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NuCode_SkillToolTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static IOptionsMonitor<NuCodeConfig> CreateMonitor(NuCodeConfig config)
    {
        return new FakeOptionsMonitor<NuCodeConfig>(config);
    }

    private static async Task<string> InvokeAsync(AIFunction fn, string name)
    {
        var result = await fn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["name"] = name,
        }));
        return result?.ToString() ?? "";
    }

    [Fact]
    public async Task LoadsSkillFromConventionDirectory()
    {
        // Create .nucode/skills/test-skill/SKILL.md
        var skillDir = Path.Combine(_tempDir, ".nucode", "skills", "test-skill");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), "# Test Skill\nDo the thing.");

        var provider = new FileSkillProvider(_tempDir, CreateMonitor(new NuCodeConfig()));
        var tool = new SkillTool(provider);
        var fn = tool.ToAIFunction();

        var text = await InvokeAsync(fn, "test-skill");

        text.ShouldContain("# Test Skill");
        text.ShouldContain("Do the thing.");
    }

    [Fact]
    public async Task LoadsSkillFromConfig()
    {
        var skillFile = Path.Combine(_tempDir, "my-skill.md");
        await File.WriteAllTextAsync(skillFile, "Configured skill content");

        var config = new NuCodeConfig
        {
            Skills = new Dictionary<string, SkillConfig>
            {
                ["my-skill"] = new SkillConfig { Path = skillFile },
            },
        };

        var provider = new FileSkillProvider(_tempDir, CreateMonitor(config));
        var tool = new SkillTool(provider);
        var fn = tool.ToAIFunction();

        var text = await InvokeAsync(fn, "my-skill");
        text.ShouldContain("Configured skill content");
    }

    [Fact]
    public async Task ReturnsErrorForUnknownSkill()
    {
        var provider = new FileSkillProvider(_tempDir, CreateMonitor(new NuCodeConfig()));
        var tool = new SkillTool(provider);
        var fn = tool.ToAIFunction();

        var text = await InvokeAsync(fn, "nonexistent");
        text.ShouldContain("not found");
    }

    [Fact]
    public async Task ListsAvailableSkillsOnError()
    {
        var skillDir = Path.Combine(_tempDir, ".nucode", "skills", "existing-skill");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), "content");

        var provider = new FileSkillProvider(_tempDir, CreateMonitor(new NuCodeConfig()));
        var tool = new SkillTool(provider);
        var fn = tool.ToAIFunction();

        var text = await InvokeAsync(fn, "wrong-name");
        text.ShouldContain("existing-skill");
    }

    [Fact]
    public async Task ReturnsErrorForEmptyName()
    {
        var provider = new FileSkillProvider(_tempDir, CreateMonitor(new NuCodeConfig()));
        var tool = new SkillTool(provider);
        var fn = tool.ToAIFunction();

        var text = await InvokeAsync(fn, "");
        text.ShouldContain("required");
    }

    [Fact]
    public void GetAvailableSkillsDiscoversBothSources()
    {
        // Convention skill
        var skillDir = Path.Combine(_tempDir, ".nucode", "skills", "conv-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "content");

        // Config skill
        var config = new NuCodeConfig
        {
            Skills = new Dictionary<string, SkillConfig>
            {
                ["cfg-skill"] = new SkillConfig { Path = "/some/path.md" },
            },
        };

        var provider = new FileSkillProvider(_tempDir, CreateMonitor(config));
        var skills = provider.GetAvailableSkills();

        skills.ShouldContain("conv-skill");
        skills.ShouldContain("cfg-skill");
    }

    private sealed class FakeOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
