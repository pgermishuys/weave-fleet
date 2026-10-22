using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Gets a new OpenCode process through the one slow thing it does before it can serve a folder. Fleet's plugin is
/// always loaded, and when OpenCode loads a plugin from a user config folder that lacks the plugin packages, it runs
/// <c>npm install @opencode-ai/plugin</c> (about 60 MB) before it answers the first request that names a folder: 30 to
/// 100 s here, measured, and past the 100 s timeout of OpenCode's ordinary HTTP client. Left to the first
/// <c>POST /session</c>, that is a user's first session failing with a timeout. So a new process loads its first folder here,
/// on a client with room for the install, and callers wait for it before they use the process.
/// </summary>
internal static partial class OpenCodeFirstRunPreparation
{
    /// <summary>How long the install may take. It only happens once per config folder, so this errs generous.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Makes OpenCode load <paramref name="directory"/>. Never throws: if this fails, the request that needs the folder
    /// loads it itself and reports whatever is wrong.
    /// </summary>
    internal static async Task RunAsync(OpenCodeHttpClient client, string directory, ILogger logger, TimeSpan timeout)
    {
        var started = TimeProvider.System.GetTimestamp();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await client.GetConfigPluginsAsync(directory, cts.Token).ConfigureAwait(false);
            var seconds = TimeProvider.System.GetElapsedTime(started).TotalSeconds;
            LogPrepared(logger, seconds);
        }
        catch (Exception ex)
        {
            var seconds = TimeProvider.System.GetElapsedTime(started).TotalSeconds;
            LogFailed(logger, seconds, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A new OpenCode process loaded its first folder in {Seconds:F1}s (a first run installs OpenCode's plugin packages).")]
    private static partial void LogPrepared(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A new OpenCode process didn't load its first folder after {Seconds:F1}s; the first request that needs it will try again.")]
    private static partial void LogFailed(ILogger logger, double seconds, Exception ex);
}
