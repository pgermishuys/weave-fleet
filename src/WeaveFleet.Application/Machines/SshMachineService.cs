using System.Text.Json;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// Adds a machine Over SSH: asks the node there for a pairing code through the user's own SSH login
/// (<c>fleet node pair --json</c>), opens a tunnel to its port, and redeems the code through the tunnel with the same
/// path as "With a pairing code". No token is typed or moved, and Fleet never answers a password prompt. Fleet only
/// reads the remote: installing Fleet there is not done here.
/// </summary>
public sealed class SshMachineService(RemoteMachineService machines, ISshTunnels tunnels, ISshRunner ssh)
{
    /// <summary>The port a node listens on unless it was started with another.</summary>
    public const int NodePort = 2113;

    private const string PairCommand = "fleet node pair --json";

    public async Task<(RemoteMachine? Machine, string? Error)> AddAsync(string destinationText, CancellationToken cancellationToken)
    {
        var destination = SshDestination.Parse(destinationText);
        if (destination is null)
            return (null, "That isn't an SSH host. Pick one from the list or type user@host.");

        var result = await ssh.RunAsync(destination, PairCommand, cancellationToken);
        var failure = Explain(destination, result);
        if (failure is not null)
            return (null, failure);

        string? code;
        try
        {
            using var document = JsonDocument.Parse(result.Output.Trim().Split('\n')[^1]);
            code = document.RootElement.GetProperty("code").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            code = null;
        }

        if (string.IsNullOrEmpty(code))
            return (null, "Fleet there answered, but not with a pairing code. Update Fleet on that machine.");

        // The tunnel is named for the code until the machine's id is known; the tunnel service then adopts it.
        var key = $"pending:{Guid.NewGuid():N}";
        var (port, openError) = await tunnels.OpenAsync(key, destination, preferredPort: null, cancellationToken);
        if (openError is not null)
            return (null, openError);

        var (machine, error) = await machines.AddByCodeAsync($"http://127.0.0.1:{port}", code, cancellationToken, destination.Text);
        if (machine is null)
            tunnels.Close(key);
        return (machine, error);
    }

    // Nothing here runs anything on the remote beyond printing a code; a missing Fleet is reported, not fixed.
    private static string? Explain(SshDestination destination, SshResult result)
    {
        var error = result.Error;
        if (result.ExitCode == 0)
            return null;
        if (result.ExitCode == -1)
            return "Fleet couldn't run ssh. Install OpenSSH and make sure ssh is on the PATH of the machine running Fleet.";
        if (result.ExitCode == 255)
        {
            if (error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
                || error.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase))
                return $"SSH doesn't trust {destination.Host} yet. Run ssh {destination.Text} once in a terminal and check its host key.";
            if (error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                return $"SSH turned your keys away at {destination.Text}. Fleet never asks for a password: load a key in ssh-agent or set IdentityFile for it in ~/.ssh/config.";
            if (error.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase))
                return $"SSH can't find {destination.Host}. Check the name.";
            return $"SSH couldn't connect to {destination.Text}. {FirstLine(error)}".TrimEnd();
        }

        if (result.ExitCode == 127
            || error.Contains("command not found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No Fleet answered", StringComparison.Ordinal))
            return $"Fleet isn't installed there. SSH works, but nothing answers on port {NodePort} at {destination.Host}.";
        return $"Fleet on {destination.Host} couldn't make a pairing code. {FirstLine(error)}".TrimEnd();
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}
