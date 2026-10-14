namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// What a Claude Code skill's <c>SKILL.md</c> says about who can run it. A skill with <c>disable-model-invocation: true</c>
/// in its frontmatter is the user's alone: the model can't load it, so a <c>$</c> mention of it can only go as <c>/name</c>.
/// </summary>
internal static class ClaudeCodeSkillFiles
{
    /// <summary>
    /// The folders Claude Code reads skills from for a session, by priority: Fleet's built-in skills, the working
    /// directory's <c>.claude/skills</c>, and the user's (<c>$CLAUDE_CONFIG_DIR/skills</c> when set, else <c>~/.claude/skills</c>).
    /// </summary>
    public static IReadOnlyList<string> Folders(string? builtInFolder, string workingDirectory)
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var user = string.IsNullOrWhiteSpace(configDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills")
            : Path.Combine(configDir, "skills");
        return [.. new[] { builtInFolder, Path.Combine(workingDirectory, ".claude", "skills"), user }.OfType<string>()];
    }

    /// <summary>Whether the first <c>&lt;folder&gt;/&lt;name&gt;/SKILL.md</c> found has <c>disable-model-invocation</c> on.</summary>
    public static bool IsUserOnly(string name, IReadOnlyList<string> folders)
    {
        if (name.AsSpan().IndexOfAny('/', '\\') >= 0 || name.Contains(".."))
            return false;

        foreach (var folder in folders)
        {
            var file = Path.Combine(folder, name, "SKILL.md");
            if (!File.Exists(file))
                continue;

            try
            {
                return DisablesModelInvocation(File.ReadLines(file));
            }
            catch (IOException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Whether the frontmatter (between the first two <c>---</c> lines) sets the switch to true, yes, on or 1.</summary>
    private static bool DisablesModelInvocation(IEnumerable<string> lines)
    {
        var inFrontmatter = false;
        foreach (var line in lines)
        {
            if (line.Trim() == "---")
            {
                if (inFrontmatter)
                    return false;
                inFrontmatter = true;
                continue;
            }

            if (!inFrontmatter)
                return false;

            const string key = "disable-model-invocation:";
            if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return line[key.Length..].Trim().Trim('"', '\'').ToLowerInvariant() is "true" or "yes" or "on" or "1";
        }

        return false;
    }
}
