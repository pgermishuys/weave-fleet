using System.Text.Json;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// Adds a machine Over SSH: asks the node there for a pairing code through the user's own SSH login
/// (<c>fleet node pair --json</c>), opens a tunnel to its port, and redeems the code through the tunnel with the same
/// path as "With a pairing code". No token is typed or moved, and Fleet never answers a password prompt. Fleet only
/// reads the remote until the user has seen <see cref="SshInstallPlan"/> and asked for the install
/// (<see cref="InstallAndAddAsync"/>): nothing is installed on the other machine on its own.
/// </summary>
public sealed class SshMachineService(RemoteMachineService machines, ISshTunnels tunnels, ISshRunner ssh, SshInstallOptions? install = null)
{
    private readonly SshInstallOptions _install = install ?? new SshInstallOptions();

    /// <summary>The port a node listens on unless it was started with another.</summary>
    public const int NodePort = 2113;

    // A non-interactive ssh has the short PATH, and the installer puts Fleet in ~/.weave/fleet/bin.
    private const string PairCommand = "env PATH=\"$HOME/.weave/fleet/bin:$PATH\" fleet node pair --json";

    public Task<SshAddResult> AddAsync(string destinationText, CancellationToken cancellationToken) =>
        AddCoreAsync(destinationText, waitForNode: false, cancellationToken);

    private async Task<SshAddResult> AddCoreAsync(string destinationText, bool waitForNode, CancellationToken cancellationToken)
    {
        var destination = SshDestination.Parse(destinationText);
        if (destination is null)
            return SshAddResult.Failed("That isn't an SSH host. Pick one from the list or type user@host.");

        var result = await ssh.RunAsync(destination, PairCommand, cancellationToken);
        // Right after an install the service may still be starting.
        for (var attempt = 0; waitForNode && attempt < _install.StartAttempts && IsNotInstalled(result); attempt++)
        {
            await Task.Delay(_install.StartPause, cancellationToken);
            result = await ssh.RunAsync(destination, PairCommand, cancellationToken);
        }

        var failure = Explain(destination, result);
        if (failure is not null && waitForNode && IsNotInstalled(result))
            return SshAddResult.Failed($"Fleet is installed on {destination.Host}, but it hasn't started answering on port {NodePort}. On that machine, journalctl --user -u fleet-node (Linux) or ~/Library/Logs/fleet-node.log (macOS) says why.");
        if (failure is not null)
            return IsNotInstalled(result) && !waitForNode
                ? new SshAddResult(null, failure, await PlanAsync(destination, cancellationToken))
                : SshAddResult.Failed(failure);

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
            return SshAddResult.Failed("Fleet there answered, but not with a pairing code. Update Fleet on that machine.");

        // The tunnel is named for the code until the machine's id is known; the tunnel service then adopts it.
        var key = $"pending:{Guid.NewGuid():N}";
        var (port, openError) = await tunnels.OpenAsync(key, destination, preferredPort: null, cancellationToken);
        if (openError is not null)
            return SshAddResult.Failed(openError);

        var (machine, error) = await machines.AddByCodeAsync($"http://127.0.0.1:{port}", code, cancellationToken, destination.Text);
        if (machine is null)
            tunnels.Close(key);
        return new SshAddResult(machine, error, null);
    }

    /// <summary>
    /// What Fleet would run on a machine that has no Fleet, so the user can read it first. Only reads the remote's
    /// operating system. Windows is not automated.
    /// </summary>
    private async Task<SshInstallPlan> PlanAsync(SshDestination destination, CancellationToken cancellationToken)
    {
        var os = await ssh.RunAsync(destination, "uname -s", cancellationToken);
        var kind = os.ExitCode == 0 ? os.Output.Trim() : "";
        var supported = kind is "Linux" or "Darwin";
        return new SshInstallPlan(destination.Text, destination.Host, kind switch { "Linux" => "linux", "Darwin" => "macos", _ => "unsupported" },
            supported, supported ? InstallCommands : [], SshInstallPlan.InstallDocs);
    }

    /// <summary>The lines shown to the user and run as one command, in this order.</summary>
    public static readonly IReadOnlyList<string> InstallCommands =
    [
        "curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh",
        "$HOME/.weave/fleet/bin/fleet node install-service",
    ];

    /// <summary>
    /// Installs the node on the other machine with <see cref="InstallCommands"/> (after the user clicked), waits for it
    /// to answer, then pairs like <see cref="AddAsync(string, CancellationToken)"/>. No sudo, no prompts: it fails in
    /// plain words instead.
    /// </summary>
    public async Task<SshAddResult> InstallAndAddAsync(string destinationText, CancellationToken cancellationToken)
    {
        var destination = SshDestination.Parse(destinationText);
        if (destination is null)
            return SshAddResult.Failed("That isn't an SSH host. Pick one from the list or type user@host.");

        var plan = await PlanAsync(destination, cancellationToken);
        if (!plan.CanInstall)
            return SshAddResult.Failed($"Fleet can install itself over SSH on Linux and macOS only. Install it on {destination.Host} yourself ({SshInstallPlan.InstallDocs}).");

        var result = await ssh.RunAsync(destination, string.Join(" && ", InstallCommands), cancellationToken, _install.Timeout);
        if (result.ExitCode != 0)
            return SshAddResult.Failed(ExplainInstall(destination, result) ?? "The install failed.");

        return await AddCoreAsync(destination.Text, waitForNode: true, cancellationToken);
    }

    private static string? ExplainInstall(SshDestination destination, SshResult result)
    {
        if (result.ExitCode == -1 || result.ExitCode == 255)
            return Explain(destination, result);
        var lastLines = string.Join(" ", result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
        var why = result.Error.Contains("curl", StringComparison.OrdinalIgnoreCase) && result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? $"{destination.Host} has no curl. Install curl there, or install Fleet yourself ({SshInstallPlan.InstallDocs})."
            : result.Error.Contains("systemctl", StringComparison.OrdinalIgnoreCase) || result.Error.Contains("systemd", StringComparison.OrdinalIgnoreCase)
                ? $"Fleet installed on {destination.Host}, but its user service couldn't start. {lastLines}".TrimEnd()
                : $"The install on {destination.Host} stopped. {lastLines}".TrimEnd();
        return why;
    }

    private static bool IsNotInstalled(SshResult result) =>
        result.ExitCode != 0 && result.ExitCode != 255 && result.ExitCode != -1
        && (result.ExitCode == 127
            || result.Error.Contains("command not found", StringComparison.OrdinalIgnoreCase)
            || result.Error.Contains("is not recognized", StringComparison.OrdinalIgnoreCase)
            || result.Error.Contains("No Fleet answered", StringComparison.Ordinal));

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

        if (IsNotInstalled(result))
            return $"Fleet isn't installed there. SSH works, but nothing answers on port {NodePort} at {destination.Host}.";
        return $"Fleet on {destination.Host} couldn't make a pairing code. {FirstLine(error)}".TrimEnd();
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
}

/// <summary>The outcome of adding a machine Over SSH: the machine, or why not, and when Fleet isn't there, what installing would run.</summary>
public sealed record SshAddResult(RemoteMachine? Machine, string? Error, SshInstallPlan? Plan)
{
    public static SshAddResult Failed(string error) => new(null, error, null);

    public void Deconstruct(out RemoteMachine? machine, out string? error)
    {
        machine = Machine;
        error = Error;
    }
}

/// <summary>What Fleet would run on <see cref="Host"/> to install the node. <see cref="Commands"/> is empty when it can't (<see cref="CanInstall"/> is false).</summary>
public sealed record SshInstallPlan(string Destination, string Host, string Os, bool CanInstall, IReadOnlyList<string> Commands, string Docs)
{
    public const string InstallDocs = "https://fleet.tryweave.io/docs";
}

/// <summary>How long the install may take, and how long Fleet waits for the new node to answer.</summary>
public sealed class SshInstallOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);
    public int StartAttempts { get; set; } = 30;
    public TimeSpan StartPause { get; set; } = TimeSpan.FromSeconds(1);
}
