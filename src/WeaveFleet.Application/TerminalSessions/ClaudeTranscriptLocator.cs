using System.Text;

namespace WeaveFleet.Application.TerminalSessions;

/// <summary>Where Claude Code keeps a session's transcript: <c>&lt;config&gt;/projects/&lt;folder slug&gt;/&lt;session id&gt;.jsonl</c>.</summary>
public static class ClaudeTranscriptLocator
{
    /// <summary>Claude Code's own folder: <c>CLAUDE_CONFIG_DIR</c>, else <c>.claude</c> in the home folder.</summary>
    public static string ConfigDirectory()
        => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>A folder's name under <c>projects</c>: every character that isn't a letter or digit becomes <c>-</c>.</summary>
    public static string Slug(string folder)
    {
        var slug = new StringBuilder(folder.Length);
        foreach (var c in folder)
            slug.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return slug.ToString();
    }

    /// <summary>Whether <paramref name="path"/> names the transcript file of session <paramref name="sessionId"/>.</summary>
    public static bool IsTranscriptOf(string? path, string sessionId)
        => path is { Length: > 0 } && Path.IsPathRooted(path)
           && string.Equals(Path.GetFileName(path), sessionId + ".jsonl", StringComparison.Ordinal);

    /// <summary>
    /// The transcript of session <paramref name="sessionId"/> run in <paramref name="folder"/>, or null when there is none:
    /// in the folder's own slug, otherwise in any project folder (a slug is made differently for a long or unusual path).
    /// </summary>
    public static string? Find(string sessionId, string folder, string? configDirectory = null)
    {
        if (sessionId.Length == 0 || sessionId.IndexOfAny(['/', '\\']) >= 0 || sessionId.Contains(".."))
            return null;

        var projects = Path.Combine(configDirectory ?? ConfigDirectory(), "projects");
        var own = Path.Combine(projects, Slug(folder), sessionId + ".jsonl");
        if (File.Exists(own))
            return own;

        try
        {
            return Directory.Exists(projects)
                ? Directory.EnumerateDirectories(projects).Select(d => Path.Combine(d, sessionId + ".jsonl")).FirstOrDefault(File.Exists)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The newest transcript in <paramref name="folder"/>'s project folder: the session whose id was never given.</summary>
    public static string? Newest(string folder, string? configDirectory = null)
    {
        var directory = Path.Combine(configDirectory ?? ConfigDirectory(), "projects", Slug(folder));
        try
        {
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
