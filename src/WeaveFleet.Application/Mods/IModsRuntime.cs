using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Mods;

/// <summary>What <see cref="IModsRuntime.BunChanged"/> says changed.</summary>
/// <param name="userId">The user whose own Bun changed; <see langword="null"/> when Fleet's own Bun was installed, which every user without a Bun of their own gets.</param>
public sealed class BunChangedEventArgs(string? userId) : EventArgs
{
    /// <summary>The user whose own Bun changed; <see langword="null"/> when Fleet's own Bun was installed.</summary>
    public string? UserId { get; } = userId;
}

/// <summary>Where installing a Bun is for ("kind"). Sent to the client as <c>kind</c>.</summary>
public static class ModsRuntimeKinds
{
    /// <summary>Fleet has no usable Bun of its own yet.</summary>
    public const string Install = "install";

    /// <summary>The release is newer than Fleet's Bun, and Fleet's Bun is still safe.</summary>
    public const string Update = "update";

    /// <summary>Fleet's Bun is older than the oldest safe version.</summary>
    public const string Security = "security";
}

/// <summary>Why a <c>mods.runtime</c> event was raised. Sent to the client as <c>reason</c>.</summary>
public static class ModsRuntimeReasons
{
    /// <summary>Progress, or a phase change of the install.</summary>
    public const string Job = "job";

    /// <summary>The install succeeded.</summary>
    public const string Installed = "installed";

    /// <summary>A user's own Bun was set or cleared.</summary>
    public const string BunPath = "bun-path";

    /// <summary>Fleet wants another release.</summary>
    public const string Release = "release";
}

/// <summary>
/// The Bun mods run on, as the Settings screen shows it: what runs now, the user's own Bun, the release Fleet wants, and
/// the install under way or the last one.
/// </summary>
/// <param name="Bun">The Bun the user's mods run on, or <see langword="null"/> when there is none (yet).</param>
/// <param name="ConfiguredPath">The user's own Bun path (their choice, or <c>Fleet:Harness:BunPath</c>), even when it can't run.</param>
/// <param name="ConfiguredError">Why <paramref name="ConfiguredPath"/> can't run, when it can't.</param>
/// <param name="ConfiguredInConfig"><see langword="true"/> when the path is <c>Fleet:Harness:BunPath</c>, which Settings can't change.</param>
/// <param name="Release">The release Fleet wants.</param>
/// <param name="InstalledSize">Bytes on disk of Fleet's installed Buns; 0 when none.</param>
/// <param name="Job">The install running now or the last one since Fleet started.</param>
/// <param name="Update">Set when Fleet's own Bun is older than the release.</param>
public sealed record ModsRuntimeView(
    ModsRuntimeBun? Bun,
    string? ConfiguredPath,
    string? ConfiguredError,
    bool ConfiguredInConfig,
    ModsRuntimeRelease Release,
    long InstalledSize,
    ModsRuntimeJob? Job,
    ModsRuntimeUpdate? Update);

/// <summary>The Bun mods run on.</summary>
/// <param name="Path">The absolute path of the executable.</param>
/// <param name="DisplayPath"><paramref name="Path"/> with the home folder shortened to <c>~</c>.</param>
/// <param name="Source"><c>installed</c> (Fleet's own) or <c>configured</c> (the user's).</param>
/// <param name="Version">Its version.</param>
/// <param name="Safe">It is the oldest safe version or later.</param>
/// <param name="Message">Why it isn't safe, when it isn't.</param>
public sealed record ModsRuntimeBun(string Path, string DisplayPath, string Source, string Version, bool Safe, string? Message);

/// <summary>The release Fleet wants.</summary>
/// <param name="Version">The Bun version.</param>
/// <param name="OldestSafe">The oldest version that is safe to run.</param>
/// <param name="Note">Why this release is recommended.</param>
/// <param name="Size">This computer's archive size in bytes; <see langword="null"/> when unknown or there is no build.</param>
/// <param name="HasBuild"><see langword="false"/> when the release has no Bun for this computer.</param>
/// <param name="InstallFolder">Where Fleet installs it, with the home folder shortened to <c>~</c>.</param>
/// <param name="Source">Where it downloads from, e.g. <c>github.com/oven-sh/bun</c>.</param>
public sealed record ModsRuntimeRelease(string Version, string OldestSafe, string? Note, long? Size, bool HasBuild, string InstallFolder, string Source);

/// <summary>Fleet's own Bun is older than the release.</summary>
/// <param name="Version">The release's version.</param>
/// <param name="Security"><see langword="true"/> when Fleet's Bun is older than the oldest safe version.</param>
public sealed record ModsRuntimeUpdate(string Version, bool Security);

/// <summary>A found Bun, with the path shortened for display.</summary>
public sealed record ModsRuntimeCandidate(string Path, string DisplayPath, string ResolvedPath, string? Version, string Status, string? Message);

/// <summary>
/// Installs and tracks the Bun mods run on, one install at a time, in the background. Telling the host that the Bun
/// changed is the job of <see cref="BunChanged"/>.
/// </summary>
public interface IModsRuntime
{
    /// <summary>
    /// Raised after an install succeeds (<see cref="BunChangedEventArgs.UserId"/> null) and after a user's own Bun path
    /// is saved or cleared: the signal that the Bun a mod host runs on may be another one.
    /// </summary>
    event EventHandler<BunChangedEventArgs>? BunChanged;

    /// <summary>The install running now or the last one since Fleet started.</summary>
    ModsRuntimeJob? Job { get; }

    /// <summary>The runtime as <paramref name="userId"/> sees it.</summary>
    Task<ModsRuntimeView> GetViewAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Installs the release's Bun in the background, or joins the install already running. Returns <see langword="false"/>
    /// when nothing needs installing. A user starting an install is remembered: if a first install fails or is cancelled
    /// and leaves them no Bun, the Mods switch goes back off for them.
    /// </summary>
    /// <param name="kind">One of <see cref="ModsRuntimeKinds"/>, or <see langword="null"/> to work it out from what's installed.</param>
    /// <param name="requestedByUserId">The user who turned Mods on; <see langword="null"/> for an install Fleet starts itself.</param>
    Task<bool> StartInstallAsync(string? kind, string? requestedByUserId, CancellationToken ct);

    /// <summary>Cancels the install running, and waits for it to end. Returns <see langword="false"/> when none runs.</summary>
    Task<bool> CancelAsync(CancellationToken ct);

    /// <summary>The Buns on this computer Fleet could offer the user; looking never makes Fleet use one.</summary>
    Task<IReadOnlyList<ModsRuntimeCandidate>> FindOnMachineAsync(CancellationToken ct);

    /// <summary>Saves (or, for <see langword="null"/>, clears) <paramref name="userId"/>'s own Bun and raises <see cref="BunChanged"/>.</summary>
    Task SaveBunPathAsync(string userId, string? path, CancellationToken ct);

    /// <summary>Tells clients that Fleet wants another release, then does what <see cref="EvaluateAsync"/> does.</summary>
    Task ReleaseChangedAsync(CancellationToken ct);

    /// <summary>Starts the install Fleet wants, when someone's mods need it and it isn't installed. Called at start-up, on a new release, and every few hours.</summary>
    Task EvaluateAsync(CancellationToken ct);
}
