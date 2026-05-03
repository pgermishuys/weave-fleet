using Microsoft.Extensions.Options;
using NuCode.Configuration;

namespace NuCode.Tools;

/// <summary>
/// Default skill provider that discovers skills from configuration and the
/// .nucode/skills/{name}/SKILL.md directory convention.
/// </summary>
internal sealed class FileSkillProvider : ISkillProvider
{
    private readonly string _workingDirectory;
    private readonly IOptionsMonitor<NuCodeConfig> _configMonitor;

    public FileSkillProvider(string workingDirectory, IOptionsMonitor<NuCodeConfig> configMonitor)
    {
        _workingDirectory = workingDirectory;
        _configMonitor = configMonitor;
    }

    public async Task<string?> GetSkillContentAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = ResolveSkillPath(name);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    public IReadOnlyList<string> GetAvailableSkills()
    {
        var skills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // From config
        var config = _configMonitor.CurrentValue;
        if (config.Skills is not null)
        {
            foreach (var name in config.Skills.Keys)
            {
                skills.Add(name);
            }
        }

        // From .nucode/skills/ directory
        var skillsDir = Path.Combine(_workingDirectory, ".nucode", "skills");
        if (Directory.Exists(skillsDir))
        {
            foreach (var dir in Directory.GetDirectories(skillsDir))
            {
                var skillFile = Path.Combine(dir, "SKILL.md");
                if (File.Exists(skillFile))
                {
                    skills.Add(Path.GetFileName(dir));
                }
            }
        }

        return skills.Order().ToList().AsReadOnly();
    }

    private string? ResolveSkillPath(string name)
    {
        // Check config first
        var config = _configMonitor.CurrentValue;
        if (config.Skills?.TryGetValue(name, out var skillConfig) == true && skillConfig.Path is not null)
        {
            var configPath = Path.IsPathRooted(skillConfig.Path)
                ? skillConfig.Path
                : Path.Combine(_workingDirectory, skillConfig.Path);
            if (File.Exists(configPath))
            {
                return configPath;
            }
        }

        // Check .nucode/skills/{name}/SKILL.md
        var conventionPath = Path.Combine(_workingDirectory, ".nucode", "skills", name, "SKILL.md");
        if (File.Exists(conventionPath))
        {
            return conventionPath;
        }

        return null;
    }
}
