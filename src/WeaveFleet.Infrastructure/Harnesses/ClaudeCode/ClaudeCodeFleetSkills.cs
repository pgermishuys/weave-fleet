using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// The built-in skills an owner turned on, in a folder a claude process takes with <c>--add-dir</c>:
/// <c>{dataDirectory}/claude-code/skills/{owner}</c>, with the skills in its <c>.claude/skills</c>. Claude Code loads an
/// added folder's skills under their own names, as OpenCode does (<c>/fleet-walkthrough</c>); a plugin's would be
/// <c>/fleet:fleet-walkthrough</c>. The user's own <c>~/.claude</c> is left alone. Claude Code lists skills when it
/// starts, so a process started before a skill was switched doesn't have it.
/// </summary>
internal sealed partial class ClaudeCodeFleetSkills(string dataDirectory, IServiceScopeFactory scopeFactory, ILogger logger)
{
    private readonly Lock _sync = new();

    /// <summary>
    /// Writes the owner's folder and returns it, or null when they turned no built-in skill on or it couldn't be
    /// written; a process then starts without it.
    /// </summary>
    public async Task<ClaudeCodeSkillsFolder?> ForOwnerAsync(string ownerUserId)
    {
        try
        {
            OwnerSkills skills;
            using (BackgroundUserContext.BeginScope(ownerUserId))
            using (var scope = scopeFactory.CreateScope())
                skills = await BuiltInSkillFiles.ReadOwnerAsync(scope.ServiceProvider, ownerUserId, ex => LogUnwritten(logger, ex)).ConfigureAwait(false);

            lock (_sync)
                return Sync(dataDirectory, ownerUserId, skills.Names, skills.Yours);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnwritten(logger, ex);
            return null;
        }
    }

    /// <summary>Makes the owner's folder hold exactly <paramref name="enabled"/>; removes it when that's none.</summary>
    internal static ClaudeCodeSkillsFolder? Sync(
        string dataDirectory, string ownerUserId, IReadOnlyList<string> enabled, IReadOnlyDictionary<string, string>? yours = null)
    {
        var root = Path.Combine(dataDirectory, "claude-code", "skills", BuiltInSkillFiles.OwnerFolder(ownerUserId));
        var names = enabled.Where(BuiltInSkillFiles.Names.Contains).Order(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            return null;
        }

        BuiltInSkillFiles.Sync(Path.Combine(root, ".claude", "skills"), names, yours);
        return new ClaudeCodeSkillsFolder(root, string.Join(',', names));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't write the built-in skills for Claude Code; its processes start without them")]
    private static partial void LogUnwritten(ILogger logger, Exception exception);
}

/// <summary>An owner's folder of built-in skills, for <c>--add-dir</c>, and the skills in it, comma-separated.</summary>
internal sealed record ClaudeCodeSkillsFolder(string Folder, string Skills);
