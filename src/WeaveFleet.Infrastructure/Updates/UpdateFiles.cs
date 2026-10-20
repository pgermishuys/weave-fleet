using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Infrastructure.Updates;

/// <summary>
/// The two small files the launcher and the app talk through, next to <c>app/</c> in an installed layout. The launcher
/// writes <c>update-trial.json</c> when it applies an update, and the new app deletes it once it has started; if it
/// doesn't, the launcher puts the old version back and writes <c>update-failed.json</c> (see scripts/launcher.sh).
/// </summary>
public sealed class UpdateFiles
{
    private readonly string? _root;

    /// <param name="installRoot">The folder holding <c>app/</c> and <c>VERSION</c>; null finds it from where the app runs (null again outside an install).</param>
    public UpdateFiles(string? installRoot = null) => _root = installRoot ?? FindInstallRoot();

    /// <summary>Whether this runs from an installed package (an <c>app/</c> folder next to a <c>VERSION</c> file).</summary>
    public bool IsInstalled => _root is not null;

    private string? TrialPath => _root is null ? null : Path.Combine(_root, "update-trial.json");

    private string? FailedPath => _root is null ? null : Path.Combine(_root, "update-failed.json");

    private static string? FindInstallRoot()
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetDirectoryName(appDir);
        return root is not null && string.Equals(Path.GetFileName(appDir), "app", StringComparison.Ordinal) && File.Exists(Path.Combine(root, "VERSION"))
            ? root
            : null;
    }

    /// <summary>The update that was applied and hasn't said it started yet.</summary>
    public UpdateTrial? ReadTrial() => Read(TrialPath, UpdateFilesJsonContext.Default.UpdateTrial);

    /// <summary>The update the launcher gave up on, if the last one failed.</summary>
    public UpdateFailure? ReadFailure() => Read(FailedPath, UpdateFilesJsonContext.Default.UpdateFailure);

    /// <summary>
    /// The new version is up: ends the trial (so the launcher keeps it), and forgets an earlier failure, which a
    /// version that came after it replaces. Does nothing when this isn't the version on trial.
    /// </summary>
    public bool ConfirmTrial(string currentVersion)
    {
        if (ReadTrial() is not { } trial || !string.Equals(trial.To, currentVersion, StringComparison.OrdinalIgnoreCase))
            return false;

        Delete(TrialPath);
        Delete(FailedPath);
        return true;
    }

    private static T? Read<T>(string? path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        if (path is null || !File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Delete(string? path)
    {
        try
        {
            if (path is not null)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The launcher tries again at the next start; a stale trial only costs an unneeded rollback check.
        }
    }
}

public sealed record UpdateTrial(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("startedAt")] string? StartedAt);

/// <param name="From">The version that was kept.</param>
/// <param name="To">The version that did not start.</param>
public sealed record UpdateFailure(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("at")] string At);

[JsonSerializable(typeof(UpdateTrial))]
[JsonSerializable(typeof(UpdateFailure))]
internal sealed partial class UpdateFilesJsonContext : JsonSerializerContext
{
}
