using WeaveFleet.Application.Browser;

namespace WeaveFleet.Application.Tests.Browser;

/// <summary>Keeps shots in memory, keyed by session, the way the file store keeps them in folders.</summary>
internal sealed class FakeScreenshotStore : ISessionScreenshotStore
{
    public Dictionary<(string SessionId, string Id), byte[]> Saved { get; } = [];

    public List<string> DeletedSessions { get; } = [];

    /// <summary>Set to fail every save, as a full disk would.</summary>
    public bool Fail { get; set; }

    public Task<string?> SaveAsync(string sessionId, byte[] png, CancellationToken ct = default)
    {
        if (Fail)
            return Task.FromResult<string?>(null);

        var id = $"shot_{Saved.Count + 1}";
        Saved[(sessionId, id)] = png;
        return Task.FromResult<string?>(id);
    }

    public Task<byte[]?> ReadAsync(string sessionId, string screenshotId, CancellationToken ct = default)
        => Task.FromResult(Saved.GetValueOrDefault((sessionId, screenshotId)));

    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        DeletedSessions.Add(sessionId);
        foreach (var key in Saved.Keys.Where(key => key.SessionId == sessionId).ToList())
            Saved.Remove(key);
        return Task.CompletedTask;
    }
}
