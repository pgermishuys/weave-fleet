using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Testing.Fakes;

/// <summary>
/// No Bun, as far as the mod host knows: a test app never starts a real mod host, whatever is installed on the machine.
/// The tests against the real host are in Infrastructure.Tests (Category=ModHostLive).
/// </summary>
public sealed class NoModHostBun : IModHostBun
{
    public Task<BunLocation?> FindAsync(CancellationToken ct) => Task.FromResult<BunLocation?>(null);
}
