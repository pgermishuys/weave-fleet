using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Machines;

/// <summary>What a machine can run, and how busy it is: <c>capabilities</c> in <c>GET /api/machine</c>.</summary>
/// <param name="Harnesses">Each harness Fleet knows, as last checked; <see langword="null"/> until the first check finishes.</param>
/// <param name="Sessions">The caller's sessions on this machine that are working or need them.</param>
/// <param name="PeerMessages">
/// Whether it takes messages from sessions on other machines (<c>/api/machine/peer</c>): a Fleet with a machine token
/// that knows the endpoints. Absent from older Fleets.
/// </param>
public sealed record MachineCapabilities(IReadOnlyList<MachineHarness>? Harnesses, MachineSessionCounts Sessions, bool PeerMessages);

/// <param name="Type">The harness type, e.g. <c>opencode</c>.</param>
/// <param name="Name">What to call it, e.g. <c>OpenCode</c>.</param>
/// <param name="Available">Whether it's installed and working on this machine.</param>
/// <param name="Enabled">Whether it's on (every harness is, until the user turns it off); a new session can use it only when it's available and on.</param>
/// <param name="Version">The version it reports, when it does.</param>
public sealed record MachineHarness(string Type, string Name, bool Available, bool Enabled, string? Version);

/// <param name="Working">Sessions in a turn.</param>
/// <param name="NeedsYou">Sessions stopped on a question or a permission ask.</param>
public sealed record MachineSessionCounts(int Working, int NeedsYou);

/// <summary>
/// Reads <see cref="MachineCapabilities"/> for the caller. Other machines ask for it every few seconds, so it never
/// starts a harness check: it reads what <see cref="HarnessAvailabilityCache"/> kept. Sessions are counted as the
/// session list shows them (top-level only, a question outranking work), and only the caller's own.
/// </summary>
public sealed class MachineCapabilitiesReader(
    FleetOptions options,
    HarnessAvailabilityCache harnesses,
    IUserPreferenceRepository preferences,
    ISessionRepository sessions,
    SessionActivityTracker activity)
{
    public async Task<MachineCapabilities> ReadAsync()
    {
        IReadOnlyList<MachineHarness>? harnessList = null;
        if (harnesses.Last is { } checkedHarnesses)
        {
            var preferenceValues = await preferences.GetAllAsync();
            harnessList = checkedHarnesses.Harnesses
                .Select(harness => new MachineHarness(
                    harness.Type,
                    harness.DisplayName,
                    harness.Available,
                    HarnessPreferences.IsEnabled(harness.Type, preferenceValues),
                    harness.Version))
                .ToList();
        }

        var working = 0;
        var needsYou = 0;
        foreach (var session in await sessions.ListActiveAsync())
        {
            if (session.ParentSessionId is not null || session.SideOfSessionId is not null)
                continue;

            switch (activity.GetEffectiveActivityStatus(session.Id))
            {
                case ActivityStatuses.WaitingInput:
                    needsYou++;
                    break;
                // Busy, retrying, delegating: anything in a turn reads "active" in the list.
                case not (null or ActivityStatuses.Idle):
                    working++;
                    break;
            }
        }

        // The same condition the peer endpoints are mapped on: only local mode has a machine token.
        var peerMessages = !options.Auth.Enabled && options.Auth.TokenAuthEnabled;
        return new MachineCapabilities(harnessList, new MachineSessionCounts(working, needsYou), peerMessages);
    }
}
