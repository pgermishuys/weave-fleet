using System.Text.Json;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// Sets a machine up from here over SSH: looks at it (<see cref="ProbeAsync"/>), then installs Fleet when it isn't there,
/// starts it in the background, asks it for a pairing code through the user's own SSH login (<c>fleet node pair --json</c>),
/// opens a tunnel to its port and redeems the code through the tunnel with the same path as a pasted pairing link
/// (<see cref="SetUpAsync"/>). No token is typed or moved, and Fleet never answers a password prompt. Fleet only reads the
/// remote until the user asks to set it up: <see cref="Commands"/> is everything that can run.
/// </summary>
public sealed class SshMachineService(RemoteMachineService machines, ISshTunnels tunnels, ISshRunner ssh, SshInstallOptions? install = null)
{
    private readonly SshInstallOptions _install = install ?? new SshInstallOptions();

    /// <summary>The port a node listens on unless it was started with another.</summary>
    public const int NodePort = 2113;

    // A non-interactive ssh has the short PATH, and the installer puts Fleet in ~/.weave/fleet/bin.
    private const string RemotePath = "env PATH=\"$HOME/.weave/fleet/bin:$PATH\"";
    private const string PairCommand = RemotePath + " fleet node pair --json";

    /// <summary>Asks a machine what it runs: its operating system, then the Fleet version, or <c>none</c>. Changes nothing.</summary>
    public const string ProbeCommand = "uname -s && { " + RemotePath + " fleet version 2>/dev/null || echo none; }";

    /// <summary>Installs Fleet where it isn't.</summary>
    public const string InstallCommand = "curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh";

    /// <summary>Starts the node as a background service that listens on 127.0.0.1 only.</summary>
    public const string StartCommand = "$HOME/.weave/fleet/bin/fleet node install-service";

    /// <summary>Everything that can run on the other machine, in order, for "Show the commands".</summary>
    public static readonly IReadOnlyList<string> Commands = [ProbeCommand, InstallCommand, StartCommand, PairCommand];

    /// <summary>Looks at a machine over SSH: whether it logs in, what it runs and whether Fleet is there.</summary>
    public async Task<SshProbe> ProbeAsync(string destinationText, CancellationToken cancellationToken)
    {
        var destination = SshDestination.Parse(destinationText);
        if (destination is null)
            return SshProbe.CantLogIn(BadDestination);
        return await ProbeAsync(destination, cancellationToken);
    }

    private async Task<SshProbe> ProbeAsync(SshDestination destination, CancellationToken cancellationToken)
    {
        var result = await ssh.RunAsync(destination, ProbeCommand, cancellationToken);
        if (result.ExitCode is -1 or 255)
            return SshProbe.CantLogIn(MachineDiagnoses.FromSsh(destination, result, MachineDiagnoses.SshStage.Login));
        if (result.ExitCode != 0)
            return SshProbe.Other("windows", MachineDiagnoses.InstallNotSupported(destination));

        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var os = lines.FirstOrDefault() switch { "Linux" => "linux", "Darwin" => "macos", _ => null };
        if (os is null)
            return SshProbe.Other("unknown", MachineDiagnoses.InstallNotSupported(destination));
        var version = lines.Length > 1 && lines[1] != "none" ? lines[1] : null;
        return new SshProbe(version is null ? SshProbeStates.NoFleet : SshProbeStates.Ready, os, version, null);
    }

    /// <summary>
    /// Sets the machine up and adds it, telling <paramref name="progress"/> about each step as it happens. Starts Fleet
    /// there when it isn't answering (installing it first when it isn't there). No sudo, no prompts: it fails with the step
    /// that broke instead.
    /// </summary>
    public async Task<SshAddResult> SetUpAsync(string destinationText, SetupProgress progress, CancellationToken cancellationToken)
    {
        var destination = SshDestination.Parse(destinationText);
        if (destination is null)
        {
            progress.Fail(SetupSteps.Login, BadDestination.Message);
            return SshAddResult.Failed(BadDestination);
        }

        progress.Begin(SetupSteps.Login);
        var probe = await ProbeAsync(destination, cancellationToken);
        if (probe.Problem is { } cantSetUp)
        {
            progress.Fail(SetupSteps.Login, cantSetUp.Diagnosis?.Headline ?? cantSetUp.Message);
            return SshAddResult.Failed(cantSetUp);
        }

        progress.Finish(SetupSteps.Login, $"Logged in to {destination.Host} · {OsLabel(probe.Os)}");

        progress.Begin(SetupSteps.Install);
        if (probe.Version is { } version)
            progress.Skip(SetupSteps.Install, $"Fleet {version} is already there");
        else
        {
            var installed = await ssh.RunAsync(destination, InstallCommand, cancellationToken, _install.Timeout);
            if (installed.ExitCode != 0)
                return Fail(progress, SetupSteps.Install, MachineDiagnoses.FromSsh(destination, installed, MachineDiagnoses.SshStage.Install));
            progress.Finish(SetupSteps.Install, $"Fleet installed on {destination.Host}");
        }

        progress.Begin(SetupSteps.Start);
        var code = await FetchCodeAsync(destination, cancellationToken);
        if (code.NotAnswering)
        {
            var started = await ssh.RunAsync(destination, StartCommand, cancellationToken, _install.Timeout);
            if (started.ExitCode != 0)
                return Fail(progress, SetupSteps.Start, MachineDiagnoses.FromSsh(destination, started, MachineDiagnoses.SshStage.Start));
            for (var attempt = 0; attempt < _install.StartAttempts && code.NotAnswering; attempt++)
            {
                await Task.Delay(_install.StartPause, cancellationToken);
                code = await FetchCodeAsync(destination, cancellationToken);
            }

            if (code.NotAnswering)
                return Fail(progress, SetupSteps.Start, MachineDiagnoses.NodeNotStarting(destination));
        }

        if (code.Problem is { } noCode)
            return Fail(progress, SetupSteps.Start, noCode);
        progress.Finish(SetupSteps.Start, "Running in the background, starts at login");

        progress.Begin(SetupSteps.Pair);
        // The tunnel is named for the code until the machine's id is known; the tunnel service then adopts it.
        var key = $"pending:{Guid.NewGuid():N}";
        var (port, openError) = await tunnels.OpenAsync(key, destination, preferredPort: null, cancellationToken);
        if (openError is not null)
            return Fail(progress, SetupSteps.Pair, openError);

        var (machine, error) = await machines.AddByCodeAsync($"http://127.0.0.1:{port}", code.Code!, cancellationToken, destination.Text);
        if (machine is null)
        {
            tunnels.Close(key);
            return Fail(progress, SetupSteps.Pair, error!);
        }

        progress.Finish(SetupSteps.Pair, $"{machine.Name} is added");
        return new SshAddResult(machine, null);
    }

    private static string OsLabel(string? os) => os switch { "linux" => "Linux", "macos" => "macOS", _ => "Unknown system" };

    private static SshAddResult Fail(SetupProgress progress, string step, MachineProblem problem)
    {
        progress.Fail(step, problem.Diagnosis?.Headline ?? problem.Message);
        return SshAddResult.Failed(problem);
    }

    private static readonly MachineProblem BadDestination = "That isn't an SSH host. Pick one from the list or type user@host.";

    /// <summary>Asks the node for a pairing code. <c>NotAnswering</c> when the command isn't there or no node answers yet.</summary>
    private async Task<FetchedCode> FetchCodeAsync(SshDestination destination, CancellationToken cancellationToken)
    {
        var result = await ssh.RunAsync(destination, PairCommand, cancellationToken);
        if (result.ExitCode == 0)
        {
            try
            {
                using var document = JsonDocument.Parse(result.Output.Trim().Split('\n')[^1]);
                if (document.RootElement.GetProperty("code").GetString() is { Length: > 0 } code)
                    return new FetchedCode(code, false, null);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
            }

            return new FetchedCode(null, false, "Fleet there answered, but not with a pairing code. Update Fleet on that machine.");
        }

        return IsNotAnswering(result)
            ? new FetchedCode(null, true, null)
            : new FetchedCode(null, false, MachineDiagnoses.FromSsh(destination, result, MachineDiagnoses.SshStage.Login));
    }

    private readonly record struct FetchedCode(string? Code, bool NotAnswering, MachineProblem? Problem);

    private static bool IsNotAnswering(SshResult result) =>
        result.ExitCode != 0 && result.ExitCode != 255 && result.ExitCode != -1
        && (result.ExitCode == 127
            || result.Error.Contains("command not found", StringComparison.OrdinalIgnoreCase)
            || result.Error.Contains("No Fleet answered", StringComparison.Ordinal));
}

/// <summary>What Fleet saw on a machine over SSH. <see cref="State"/> is one of <see cref="SshProbeStates"/>.</summary>
public sealed record SshProbe(string State, string? Os, string? Version, MachineProblem? Problem)
{
    public static SshProbe CantLogIn(MachineProblem problem) => new(SshProbeStates.CantLogIn, null, null, problem);

    public static SshProbe Other(string os, MachineProblem problem) => new(SshProbeStates.Unsupported, os, null, problem);
}

public static class SshProbeStates
{
    /// <summary>SSH works and Fleet is installed.</summary>
    public const string Ready = "ready";

    /// <summary>SSH works and Fleet isn't installed.</summary>
    public const string NoFleet = "noFleet";

    /// <summary>SSH doesn't log in.</summary>
    public const string CantLogIn = "cantLogIn";

    /// <summary>SSH works, but it is not a Linux or macOS machine, so Fleet can't set it up.</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>The outcome of setting a machine up over SSH: the machine, or why not.</summary>
public sealed record SshAddResult(RemoteMachine? Machine, MachineProblem? Problem)
{
    public string? Error => Problem?.Message;

    public MachineDiagnosis? Diagnosis => Problem?.Diagnosis;

    public static SshAddResult Failed(MachineProblem problem) => new(null, problem);
}

/// <summary>How long the install may take, and how long Fleet waits for the new node to answer.</summary>
public sealed class SshInstallOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);
    public int StartAttempts { get; set; } = 30;
    public TimeSpan StartPause { get; set; } = TimeSpan.FromSeconds(1);
}
