using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Infrastructure.Users;

namespace WeaveFleet.Infrastructure.Harnesses.Copilot;

/// <summary>
/// Fleet's own skills (<c>fleet</c>) and the built-in skills an owner turned on, in a folder weave-copilot hands to
/// Copilot as a skill directory: <c>{dataDirectory}/copilot/skills/{owner}</c>, with the skill folders directly in it.
/// The user's own Copilot skills are left alone. Copilot reads the folder when a session starts or loads.
/// </summary>
internal sealed partial class CopilotFleetSkills(string dataDirectory, IServiceScopeFactory scopeFactory, ILogger logger)
{
    private readonly Lock _sync = new();

    /// <summary>Writes the owner's folder and returns it, or null when it couldn't be written; a session then starts without it.</summary>
    public async Task<string?> ForOwnerAsync(string ownerUserId)
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

    /// <summary>Makes the owner's folder hold Fleet's own skills and exactly the built-in skills in <paramref name="enabled"/>.</summary>
    internal static string Sync(
        string dataDirectory, string ownerUserId, IReadOnlyList<string> enabled, IReadOnlyDictionary<string, string>? yours = null)
    {
        var root = Path.Combine(dataDirectory, "copilot", "skills", BuiltInSkillFiles.OwnerFolder(ownerUserId));
        BuiltInSkillFiles.Sync(root, enabled.Where(BuiltInSkillFiles.Names.Contains).Order(StringComparer.Ordinal).ToList(), yours, withFleetSkills: true);
        return root;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't write the built-in skills for GitHub Copilot; its sessions start without them")]
    private static partial void LogUnwritten(ILogger logger, Exception exception);
}
