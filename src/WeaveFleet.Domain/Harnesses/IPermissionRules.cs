using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Harnesses;

/// <summary>
/// One session's "Don't ask again" rules as its harness session sees them: read when an ask comes in, added when the
/// user answers "always". Fleet keeps them in its database so they outlast the harness session and a restart, and the
/// user can list and remove them.
/// </summary>
public interface IPermissionRules
{
    /// <summary>The rules now, oldest first. Changes made elsewhere (the user removing one) show here at once.</summary>
    IReadOnlyList<SessionPermissionRule> Current { get; }

    /// <summary>Reads the session's rules, so <see cref="Current"/> has them.</summary>
    Task LoadAsync(CancellationToken ct);

    /// <summary>Saves a rule allowing <paramref name="tool"/> on <paramref name="patterns"/>.</summary>
    Task AddAsync(string tool, IReadOnlyList<string> patterns, CancellationToken ct);
}
