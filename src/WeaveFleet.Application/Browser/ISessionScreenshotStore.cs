namespace WeaveFleet.Application.Browser;

/// <summary>
/// Where a screenshot an agent took is, so the conversation can show the user what the agent saw. It rides on
/// the tool's metadata, which every harness keeps with the call, so it comes back after a reload.
/// <paramref name="SessionId"/> is the session the shot is kept under: a sub-agent's call counts as its parent's.
/// </summary>
public sealed record ScreenshotReference(string SessionId, string Id, int Width, int Height);

/// <summary>
/// Keeps each session's agent screenshots on disk. The harness hands the image to the model and nothing else;
/// Fleet keeps its own copy for the user, until the session is deleted.
/// </summary>
public interface ISessionScreenshotStore
{
    /// <summary>Saves the PNG and returns its id, or null when it couldn't be written (the agent still gets its image).</summary>
    Task<string?> SaveAsync(string sessionId, byte[] png, CancellationToken ct = default);

    /// <summary>The PNG, or null when the session has no screenshot by that id.</summary>
    Task<byte[]?> ReadAsync(string sessionId, string screenshotId, CancellationToken ct = default);

    /// <summary>Deletes every screenshot kept for the session.</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);
}
