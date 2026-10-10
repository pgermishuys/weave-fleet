using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>Reads the Mods switch as the user, in a scope of its own, since the host runs outside any request.</summary>
public sealed class ScopedModUserGate(IServiceScopeFactory scopes, ModsSafeMode safeMode) : IModUserGate
{
    public async Task<bool> IsSwitchedOnAsync(string userId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
        return await scope.ServiceProvider.GetRequiredService<ModsFeature>().IsSwitchedOnAsync().ConfigureAwait(false);
    }

    public bool IsSafeMode(string userId) => safeMode.IsOn(userId);
}

/// <summary>Turns a mod off after three strikes through the user's own <see cref="ModService"/>, so <c>mods.changed</c> is raised as for any change.</summary>
public sealed class ScopedModStrikeRecorder(IServiceScopeFactory scopes) : IModStrikeRecorder
{
    public Task RecordKeptAsync(string userId, string name, string message, CancellationToken ct)
        => AsUserAsync(userId, mods => mods.RecordStrikesAsync(name, message, ct));

    public Task RecordDraftAsync(string userId, string sessionId, string name, string message, CancellationToken ct)
        => AsUserAsync(userId, mods => mods.RecordDraftStrikesAsync(sessionId, name, message, ct));

    private async Task AsUserAsync(string userId, Func<ModService, Task> record)
    {
        using var scope = scopes.CreateScope();
        using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
        await record(scope.ServiceProvider.GetRequiredService<ModService>()).ConfigureAwait(false);
    }
}

/// <summary>The Bun the host runs on, from <see cref="IBunRuntime"/>: found, never installed here.</summary>
public sealed class BunModHostBun(IBunRuntime bun) : IModHostBun
{
    public Task<BunLocation?> FindAsync(CancellationToken ct) => Task.FromResult(bun.Find());

    // Fleet keeps one installed version until the runtime can hold several (M4b); there's nothing older to prune yet.
    public Task PruneAsync(IReadOnlyCollection<string> inUse, CancellationToken ct) => Task.CompletedTask;
}
