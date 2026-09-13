namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A command Fleet runs for a session, usually a dev server, as last recorded. The live process, its ports
/// and its output are only in memory; this is what's left of a run after Fleet restarts.
/// </summary>
public sealed class AppRun
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    /// <summary>Owner's user identifier.</summary>
    public string UserId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    /// <summary>The <c>PORT</c> the command gets, the same on every start.</summary>
    public int Port { get; set; }
    /// <summary><c>starting</c>, <c>running</c>, <c>exited</c> or <c>stopped</c>.</summary>
    public string Status { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
    public string? Url { get; set; }
    /// <summary>The process Fleet last started for this run, while it may still be running.</summary>
    public int? Pid { get; set; }
    /// <summary>When <see cref="Pid"/> started, to tell it apart from a later process that reuses the id.</summary>
    public string? PidStartedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}
