using System.Text.Json;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Testing.Fakes;

/// <summary>
/// A scriptable <see cref="IModHost"/>: answers dispatches with <see cref="OnDispatch"/> (not dispatched by default),
/// checks with <see cref="CheckReport"/> (or refuses with <see cref="NotReadyReason"/>), and records every call.
/// </summary>
public sealed class FakeModHost : IModHost
{
    private readonly Lock _sync = new();
    private readonly List<(string UserId, ModDispatchRequest Request)> _dispatches = [];
    private readonly List<(string UserId, string Folder)> _checks = [];
    private readonly List<(string UserId, string SessionId)> _forgotten = [];
    private readonly List<string> _ensured = [];

    /// <summary>What <see cref="GetStatus"/> answers, for every user.</summary>
    public ModHostStatus Status { get; set; } = ModHostStatus.StoppedWith(null);

    /// <summary>Answers a dispatch; null answers <see cref="ModDispatchResult.NotDispatched"/>.</summary>
    public Func<string, ModDispatchRequest, ModDispatchResult>? OnDispatch { get; set; }

    /// <summary>What <see cref="CheckAsync"/> answers when <see cref="NotReadyReason"/> is null.</summary>
    public JsonElement CheckReport { get; set; } = JsonDocument.Parse("""{ "ok": true }""").RootElement.Clone();

    /// <summary>When set, <see cref="CheckAsync"/> throws <see cref="ModHostNotReadyException"/> with it.</summary>
    public string? NotReadyReason { get; set; }

    /// <summary>Load problems by (user, mod id).</summary>
    public Dictionary<(string UserId, string ModId), ModLoadProblem> Problems { get; } = [];

    /// <summary>Log lines by (user, mod id).</summary>
    public Dictionary<(string UserId, string ModId), List<ModLogLine>> Logs { get; } = [];

    public IReadOnlyList<(string UserId, ModDispatchRequest Request)> Dispatches
    {
        get
        {
            lock (_sync)
                return [.. _dispatches];
        }
    }

    public IReadOnlyList<(string UserId, string Folder)> Checks
    {
        get
        {
            lock (_sync)
                return [.. _checks];
        }
    }

    public IReadOnlyList<(string UserId, string SessionId)> Forgotten
    {
        get
        {
            lock (_sync)
                return [.. _forgotten];
        }
    }

    public IReadOnlyList<string> Ensured
    {
        get
        {
            lock (_sync)
                return [.. _ensured];
        }
    }

    public ModHostStatus GetStatus(string userId) => Status;

    public Task EnsureAsync(string userId, CancellationToken ct = default)
    {
        lock (_sync)
            _ensured.Add(userId);
        return Task.CompletedTask;
    }

    public Task<JsonElement> CheckAsync(string userId, string folder, CancellationToken ct = default)
    {
        lock (_sync)
            _checks.Add((userId, folder));
        return NotReadyReason is { } reason
            ? Task.FromException<JsonElement>(new ModHostNotReadyException(reason))
            : Task.FromResult(CheckReport);
    }

    public Task<ModDispatchResult> DispatchAsync(string userId, ModDispatchRequest request, CancellationToken ct = default)
    {
        lock (_sync)
            _dispatches.Add((userId, request));
        return Task.FromResult(OnDispatch?.Invoke(userId, request) ?? ModDispatchResult.NotDispatched);
    }

    public Task ForgetSessionAsync(string userId, string sessionId, CancellationToken ct = default)
    {
        lock (_sync)
            _forgotten.Add((userId, sessionId));
        return Task.CompletedTask;
    }

    public ModLoadProblem? GetLoadProblem(string userId, string modId)
    {
        lock (_sync)
            return Problems.GetValueOrDefault((userId, modId));
    }

    public IReadOnlyList<ModLogLine> GetLog(string userId, string modId)
    {
        lock (_sync)
            return Logs.TryGetValue((userId, modId), out var lines) ? [.. lines] : [];
    }
}
