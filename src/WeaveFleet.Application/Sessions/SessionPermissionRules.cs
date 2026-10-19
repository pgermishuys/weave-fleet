using System.Collections.Concurrent;
using System.Text.Json;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>A rule as the client shows it.</summary>
public sealed record PermissionRuleView(string Id, string Kind, string Tool, IReadOnlyList<string> Patterns, string CreatedAt)
{
    public static PermissionRuleView From(SessionPermissionRule rule)
        => new(rule.Id, rule.Kind, rule.Tool, rule.Patterns, rule.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>The <c>session.permission-rules</c> event: a session's whole list, sent whenever it changes.</summary>
public sealed record SessionPermissionRulesChanged(string SessionId, IReadOnlyList<PermissionRuleView> Items);

/// <summary>
/// What each session's user said not to ask again about, kept in Fleet's database so a restart or a reloaded session
/// doesn't forget it. A copy lives in memory, shared by the API (list, remove) and the sessions' permission gates
/// (<see cref="IPermissionRules"/>), so a rule removed here applies to the very next ask. Every change goes out as
/// <see cref="ChangedEvent"/> on the session's topic.
/// </summary>
public sealed class SessionPermissionRules(ISessionPermissionRuleRepository repository, IEventBroadcaster broadcaster)
{
    /// <summary>The event carrying a session's rules after they changed (<see cref="SessionPermissionRulesChanged"/>).</summary>
    public const string ChangedEvent = "session.permission-rules";

    private readonly ConcurrentDictionary<string, IReadOnlyList<SessionPermissionRule>> _cache = new(StringComparer.Ordinal);

    /// <summary>The session's rules, read from the database the first time.</summary>
    public async Task<IReadOnlyList<SessionPermissionRule>> ListAsync(string sessionId)
    {
        if (_cache.TryGetValue(sessionId, out var cached))
            return cached;
        var rules = await repository.ListAsync(sessionId).ConfigureAwait(false);
        return _cache.GetOrAdd(sessionId, rules);
    }

    /// <summary>The rules the session has now, or none when they haven't been read yet.</summary>
    public IReadOnlyList<SessionPermissionRule> Current(string sessionId)
        => _cache.TryGetValue(sessionId, out var cached) ? cached : [];

    /// <summary>The rules of <paramref name="sessionId"/> as its harness session sees them.</summary>
    public IPermissionRules ForSession(string sessionId) => new SessionRules(this, sessionId);

    public async Task AddAsync(string sessionId, string tool, IReadOnlyList<string> patterns, CancellationToken ct = default)
    {
        var rule = new SessionPermissionRule
        {
            Id = $"rule_{Guid.NewGuid():N}",
            SessionId = sessionId,
            Kind = PermissionKinds.Classify(tool),
            Tool = tool,
            Patterns = patterns.Count > 0 ? patterns : ["*"],
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (!await repository.AddAsync(rule).ConfigureAwait(false))
            return;
        await ReloadAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>Removes one rule; false when the session has no such rule.</summary>
    public async Task<bool> RemoveAsync(string sessionId, string ruleId, CancellationToken ct = default)
    {
        if (!await repository.DeleteAsync(sessionId, ruleId).ConfigureAwait(false))
            return false;
        await ReloadAsync(sessionId, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Removes every rule of the session.</summary>
    public async Task ClearAsync(string sessionId, CancellationToken ct = default)
    {
        if (await repository.DeleteAllAsync(sessionId).ConfigureAwait(false) > 0)
            await ReloadAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <summary>A fork carries on the parent's line of work, so it keeps the parent's rules.</summary>
    public async Task CopyAsync(string fromSessionId, string toSessionId, CancellationToken ct = default)
    {
        await repository.CopyAsync(fromSessionId, toSessionId).ConfigureAwait(false);
        _cache.TryRemove(toSessionId, out _);
        await BroadcastAsync(toSessionId, await ListAsync(toSessionId).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    private async Task ReloadAsync(string sessionId, CancellationToken ct)
    {
        var rules = await repository.ListAsync(sessionId).ConfigureAwait(false);
        _cache[sessionId] = rules;
        await BroadcastAsync(sessionId, rules, ct).ConfigureAwait(false);
    }

    private async Task BroadcastAsync(string sessionId, IReadOnlyList<SessionPermissionRule> rules, CancellationToken ct)
    {
        if (await repository.OwnerAsync(sessionId).ConfigureAwait(false) is not { } userId)
            return;
        var payload = JsonSerializer.SerializeToElement(
            new SessionPermissionRulesChanged(sessionId, rules.Select(PermissionRuleView.From).ToList()),
            ApplicationJsonContext.Default.SessionPermissionRulesChanged);
        await broadcaster.BroadcastAsync($"session:{sessionId}", ChangedEvent, payload, userId, ct).ConfigureAwait(false);
    }

    private sealed class SessionRules(SessionPermissionRules owner, string sessionId) : IPermissionRules
    {
        public IReadOnlyList<SessionPermissionRule> Current => owner.Current(sessionId);

        public Task LoadAsync(CancellationToken ct) => owner.ListAsync(sessionId);

        public Task AddAsync(string tool, IReadOnlyList<string> patterns, CancellationToken ct)
            => owner.AddAsync(sessionId, tool, patterns, ct);
    }
}
