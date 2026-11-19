using System.Text.Json;

namespace WeaveFleet.Application.Machines;

/// <summary>States of Tailscale on this machine, from <see cref="TailscaleChecks.Read"/>.</summary>
public static class TailscaleStates
{
    /// <summary>Tailscale isn't installed, or its service isn't running.</summary>
    public const string Missing = "missing";
    /// <summary>Installed and signed in, switched off.</summary>
    public const string Stopped = "stopped";
    /// <summary>Installed, but nobody is signed in (or the machine waits for approval).</summary>
    public const string SignedOut = "signed-out";
    public const string Running = "running";
}

/// <summary>
/// What Fleet found about Tailscale on this machine, in words to show. <see cref="Covered"/> says whether the reach
/// listener already covers the Tailscale address; <see cref="CanTurnOn"/> is true when Tailscale is fine and only the
/// listener is missing, which one button fixes.
/// </summary>
public sealed record TailscaleCheck(string State, string Headline, string Detail, bool Covered, bool CanTurnOn);

public static class TailscaleChecks
{
    /// <summary>Reads the JSON of <c>tailscale status --json</c> (null when it didn't answer) and whether the listener covers Tailscale.</summary>
    public static TailscaleCheck Read(string? statusJson, string machineName, bool listenerCoversTailnet)
    {
        var state = StateOf(statusJson);
        return state switch
        {
            TailscaleStates.Running when listenerCoversTailnet => new(state, "Tailscale is on", $"Phones on your tailnet can reach {machineName}.", true, false),
            TailscaleStates.Running => new(state, "Fleet isn't listening on Tailscale yet",
                $"Tailscale is running here, but {machineName} only listens on this machine. Turn it on so phones on your tailnet can reach it.", false, true),
            TailscaleStates.Stopped => new(state, "Tailscale is switched off on this machine",
                "Switch it on in the Tailscale app (or run tailscale up), then check again.", false, false),
            TailscaleStates.SignedOut => new(state, "Tailscale isn't signed in on this machine",
                "Sign in in the Tailscale app (or run tailscale up), then check again.", false, false),
            _ => new(TailscaleStates.Missing, "Fleet can't find Tailscale on this machine",
                "Install Tailscale from tailscale.com/download, or start its service, then check again. The home network works without it.", false, false),
        };
    }

    private static string StateOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return TailscaleStates.Missing;
        try
        {
            using var document = JsonDocument.Parse(json);
            var backend = document.RootElement.TryGetProperty("BackendState", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return backend switch
            {
                "Running" => TailscaleStates.Running,
                "Stopped" => TailscaleStates.Stopped,
                "NeedsLogin" or "NeedsMachineAuth" => TailscaleStates.SignedOut,
                _ => TailscaleStates.Missing,
            };
        }
        catch (JsonException)
        {
            return TailscaleStates.Missing;
        }
    }
}
