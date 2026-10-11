using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Mods;

/// <summary>Why a <c>mods.runtime</c> event was raised. Sent to the client as <c>reason</c>.</summary>
public static class ModsRuntimeReasons
{
    /// <summary>Progress, or a phase change of the install.</summary>
    public const string Job = "job";

    /// <summary>The install succeeded.</summary>
    public const string Installed = "installed";
}

/// <summary>The Bun mods run on, as the Settings screen shows it.</summary>
/// <param name="Bun">The Bun mods run on, or <see langword="null"/> when there is none (yet).</param>
/// <param name="ConfiguredPath"><c>Fleet:Harness:BunPath</c> when set, even when it can't run.</param>
/// <param name="Job">The install running now or the last one since Fleet started.</param>
public sealed record ModsRuntimeView(
    ModsRuntimeBun? Bun,
    string? ConfiguredPath,
    ModsRuntimeRelease Release,
    ModsRuntimeJob? Job);

/// <summary>The Bun mods run on.</summary>
/// <param name="DisplayPath">The path with the home folder shortened to <c>~</c>.</param>
/// <param name="Source"><c>installed</c> (Fleet's own) or <c>configured</c>.</param>
/// <param name="Safe">It is the oldest safe version or later; <paramref name="Message"/> says why not.</param>
public sealed record ModsRuntimeBun(string Path, string DisplayPath, string Source, string Version, bool Safe, string? Message);

/// <summary>The release Fleet wants.</summary>
/// <param name="Size">This computer's archive size in bytes, when known.</param>
/// <param name="HasBuild"><see langword="false"/> when the release has no Bun for this computer.</param>
/// <param name="InstallFolder">Where Fleet installs it, shortened with <c>~</c>.</param>
/// <param name="Source">Where it downloads from, e.g. <c>github.com/oven-sh/bun</c>.</param>
public sealed record ModsRuntimeRelease(string Version, long? Size, bool HasBuild, string InstallFolder, string Source);

/// <summary>
/// Installs and tracks the Bun mods run on, one install at a time, in the background.
/// </summary>
public interface IModsRuntime
{
    /// <summary>The install running now or the last one since Fleet started.</summary>
    ModsRuntimeJob? Job { get; }

    /// <summary>The runtime as the Settings screen shows it.</summary>
    Task<ModsRuntimeView> GetViewAsync(CancellationToken ct);

    /// <summary>
    /// Installs the release's Bun in the background for <paramref name="userId"/>, or joins the install already
    /// running. Returns <see langword="false"/> when nothing needs installing. If the first install fails or is
    /// cancelled and leaves no Bun, the Mods switch goes back off for the users who asked for it.
    /// </summary>
    Task<bool> StartInstallAsync(string userId, CancellationToken ct);

    /// <summary>Cancels the install running, and waits for it to end. Returns <see langword="false"/> when none runs.</summary>
    Task<bool> CancelAsync(CancellationToken ct);
}
