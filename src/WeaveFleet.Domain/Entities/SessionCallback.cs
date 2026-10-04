namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A callback registered to notify one session when another session completes.
/// </summary>
public sealed class SessionCallback
{
    public string Id { get; set; } = string.Empty;
    public string SourceSessionId { get; set; } = string.Empty;
    public string TargetSessionId { get; set; } = string.Empty;

    /// <summary>
    /// The target's instance when the callback was registered. Instances change when Fleet restarts or the session is
    /// resumed, so firing goes to the target session's current instance instead.
    /// </summary>
    public string TargetInstanceId { get; set; } = string.Empty;

    /// <summary>One of <see cref="SessionCallbackStatuses"/>.</summary>
    public string Status { get; set; } = SessionCallbackStatuses.Pending;
    public string CreatedAt { get; set; } = string.Empty;
    public string? FiredAt { get; set; }
}

/// <summary>Where a <see cref="SessionCallback"/> is. It moves forward only: pending, started, fired.</summary>
public static class SessionCallbackStatuses
{
    /// <summary>The source session hasn't started working yet.</summary>
    public const string Pending = "pending";

    /// <summary>
    /// The source session has worked, so the callback fires once it isn't in a turn. Kept in the database so a turn
    /// that a Fleet restart ended still fires it.
    /// </summary>
    public const string Started = "started";

    /// <summary>The target session was prompted. Set only once the prompt was delivered.</summary>
    public const string Fired = "fired";
}
