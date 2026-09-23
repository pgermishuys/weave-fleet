using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;

namespace WeaveFleet.Application.Harnesses;

/// <summary>
/// What a harness offers in a folder (agents, models, commands) changed while it ran: the user added an agent file,
/// edited config or signed in to a provider.
/// </summary>
/// <param name="HarnessType">The harness, e.g. <c>opencode2</c>.</param>
/// <param name="OwnerUserId">Whose harness it is; only their browsers hear about it.</param>
/// <param name="Directory">The folder whose catalog changed.</param>
/// <param name="ProfileIds">
/// The profiles whose catalog in the folder this is, by id, with <see cref="HarnessProfileService.NoProfile"/> for
/// sessions without one. Empty when the harness can't tell.
/// </param>
/// <param name="SessionIds">The Fleet sessions in the folder that get this catalog, for their slash-command lists.</param>
public sealed record HarnessCatalogChange(
    string HarnessType,
    string OwnerUserId,
    string Directory,
    IReadOnlyList<string> ProfileIds,
    IReadOnlyList<string> SessionIds);

/// <summary>The <see cref="HarnessCatalogChanges.EventType"/> payload on the global sessions topic.</summary>
public sealed record HarnessCatalogChangedPayload
{
    public required string HarnessType { get; init; }

    public required string Directory { get; init; }

    /// <summary>The folder is where quick chats run, which the composer asks for without naming a folder.</summary>
    public bool QuickChat { get; init; }

    public required IReadOnlyList<string> ProfileIds { get; init; }

    public required IReadOnlyList<string> SessionIds { get; init; }
}

/// <summary>
/// Tells the owner's browsers that a harness's catalog changed in a folder, so an open new-session composer or a
/// session's slash-command list asks again instead of showing the old list until it's reopened. A harness that can
/// tell calls this; one that can't never does, and its lists refresh when they're reopened.
/// </summary>
public sealed partial class HarnessCatalogChanges(IEventBroadcaster broadcaster, ILogger<HarnessCatalogChanges> logger)
{
    /// <summary>The event type on the global sessions topic.</summary>
    public const string EventType = "harness.catalog_changed";

    public async Task PublishAsync(HarnessCatalogChange change, CancellationToken ct)
    {
        var directory = Path.TrimEndingDirectorySeparator(change.Directory);
        var payload = new HarnessCatalogChangedPayload
        {
            HarnessType = change.HarnessType,
            Directory = directory,
            QuickChat = string.Equals(directory, Path.TrimEndingDirectorySeparator(QuickChatSessionSourceProvider.BasePath), StringComparison.Ordinal),
            ProfileIds = change.ProfileIds,
            SessionIds = change.SessionIds,
        };

        try
        {
            await broadcaster.BroadcastAsync(
                "sessions",
                EventType,
                JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.HarnessCatalogChangedPayload),
                change.OwnerUserId,
                ct).ConfigureAwait(false);
            LogPublished(change.HarnessType, directory, change.SessionIds.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPublishFailed(ex, change.HarnessType, directory);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The {HarnessType} catalog changed in {Directory} ({Sessions} session(s) there)")]
    private partial void LogPublished(string harnessType, string directory, int sessions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't tell the browsers the {HarnessType} catalog changed in {Directory}")]
    private partial void LogPublishFailed(Exception ex, string harnessType, string directory);
}
