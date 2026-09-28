using System.Collections.Concurrent;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>In-memory <see cref="IPendingPermissions"/>: an ask lives only as long as the harness that asked it.</summary>
public sealed class PendingPermissionStore : IPendingPermissions
{
    private readonly ConcurrentDictionary<string, (string ShownOn, PermissionAsk Ask)> _asks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<PermissionAsk> WaitingIn(string sessionId)
        => [.. _asks.Values.Where(entry => entry.ShownOn == sessionId).Select(entry => entry.Ask).OrderBy(ask => ask.AskedAt)];

    /// <summary>Remembers <paramref name="ask"/>, shown on session <paramref name="shownOn"/>.</summary>
    public void Asked(string shownOn, PermissionAsk ask) => _asks[ask.Id] = (shownOn, ask);

    /// <summary>Forgets ask <paramref name="id"/>: it was answered, or went away.</summary>
    public void Replied(string id) => _asks.TryRemove(id, out _);

    /// <summary>Forgets the asks session <paramref name="sessionId"/>'s harness asked, which went away with it.</summary>
    public IReadOnlyList<(string ShownOn, PermissionAsk Ask)> ForgetAskedBy(string sessionId)
    {
        var gone = _asks.Values.Where(entry => entry.Ask.SessionId == sessionId).ToList();
        foreach (var entry in gone)
            _asks.TryRemove(entry.Ask.Id, out _);
        return gone;
    }
}
