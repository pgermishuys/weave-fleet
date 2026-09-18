using System.Runtime.InteropServices;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// Finds a Chrome, Edge or Chromium already installed on this machine. Fleet never downloads a browser:
/// screenshots are a convenience, and a 150 MB download in the middle of a tool call is not.
/// </summary>
public static class ChromeFinder
{
    /// <summary>Told to the agent, and to the user in the log, when there's nothing to drive.</summary>
    public const string NotFound =
        "No Chrome, Edge or Chromium on this machine, so Fleet can't take screenshots. Install Google Chrome, "
        + "or point Fleet at a browser with Browser:ChromePath in its configuration.";

    /// <summary>The first browser that exists, or null. <paramref name="configured"/> wins when it's set.</summary>
    public static string? Find(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        foreach (var candidate in Candidates())
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            else if (OnPath(candidate) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            })
            {
                if (string.IsNullOrEmpty(root))
                    continue;
                yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
                yield return Path.Combine(root, "Chromium", "Application", "chrome.exe");
            }

            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
            yield return "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
                yield return Path.Combine(home, "Applications", "Google Chrome.app", "Contents", "MacOS", "Google Chrome");
            yield break;
        }

        yield return "/opt/google/chrome/chrome";
        yield return "/opt/microsoft/msedge/msedge";
        yield return "/usr/bin/chromium";
        yield return "/usr/bin/chromium-browser";
        yield return "/snap/bin/chromium";
        // Names on PATH last: a wrapper script is fine, and this covers unusual installs.
        yield return "google-chrome";
        yield return "google-chrome-stable";
        yield return "chromium";
        yield return "chromium-browser";
        yield return "microsoft-edge";
    }

    private static string? OnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder.Trim(), name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
