namespace WeaveFleet.Application.Services;

/// <summary>The lifecycle status of an in-app update check or download.</summary>
public enum UpdateStatus
{
    /// <summary>No check has been performed yet.</summary>
    Unknown,

    /// <summary>The installed version is the latest available.</summary>
    UpToDate,

    /// <summary>A newer version is available but not yet downloaded.</summary>
    Available,

    /// <summary>The update archive is currently being downloaded.</summary>
    Downloading,

    /// <summary>The update has been downloaded, validated, and staged for the next restart.</summary>
    Staged,

    /// <summary>An error occurred during the check or download.</summary>
    Error,
}

/// <summary>Immutable snapshot of the current update state.</summary>
public sealed record UpdateState(
    UpdateStatus Status,
    string? LatestVersion,
    string? DownloadUrl,
    string? AssetName,
    DateTimeOffset? CheckedAt,
    string? Error,
    long? DownloadBytesReceived = null,
    long? DownloadBytesTotal = null)
{
    /// <summary>Initial state before any check has run.</summary>
    public static readonly UpdateState Initial = new(
        UpdateStatus.Unknown,
        LatestVersion: null,
        DownloadUrl: null,
        AssetName: null,
        CheckedAt: null,
        Error: null);
}
