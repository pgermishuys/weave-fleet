using System.Text.Json;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>
/// The static check, run by the current user's mod host. When no host can run, the check fails with the reason as a
/// <see cref="ModStoreException"/>, so Keep and <c>/check</c> refuse with it instead of keeping without a report.
/// </summary>
public sealed class HostModChecker(IModHost host, IUserContext user) : IModChecker
{
    public async Task<JsonElement?> CheckAsync(string folder, CancellationToken ct = default)
    {
        try
        {
            return await host.CheckAsync(user.UserId, folder, ct).ConfigureAwait(false);
        }
        catch (ModHostNotReadyException e)
        {
            throw new ModStoreException($"The mod runtime isn't ready yet: {e.Message}");
        }
    }
}
