namespace WeaveFleet.Application.Harnesses;

/// <summary>Installs and updates a harness from downloads, at the versions this Fleet release was built against.</summary>
public interface IHarnessInstaller
{
    /// <summary>The version of the harness itself that Fleet installs. An older install is offered an update.</summary>
    string TargetVersion { get; }

    /// <summary>What Fleet downloads and where it puts it, as a sentence for the install and update offers.</summary>
    string Plan { get; }

    /// <summary>
    /// Downloads, checks and installs whatever is missing, reporting what it's doing as a short line.
    /// Throws <see cref="HarnessInstallException"/> with a message fit to show the user.
    /// </summary>
    Task InstallAsync(IProgress<string> progress, CancellationToken ct);
}

/// <summary>An install that failed; the message says why in words for the user.</summary>
public sealed class HarnessInstallException(string message, Exception? inner = null) : Exception(message, inner);
