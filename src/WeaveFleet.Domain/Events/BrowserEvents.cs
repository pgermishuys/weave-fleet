namespace WeaveFleet.Domain.Events;

/// <summary>
/// Raised when an agent took an action in its own browser tab (opened a page, clicked, typed, read the page). The
/// conversation lists the steps under the tool call that made them, and Agent's view captions the latest.
/// </summary>
public sealed record BrowserStepped : DomainEvent
{
    public required BrowserStep Payload { get; init; }
}

/// <summary>One browser action, as the user sees it.</summary>
public sealed record BrowserStep
{
    public required string SessionId { get; init; }

    /// <summary>Its place in the session's steps, from 1.</summary>
    public required long Seq { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>The operation, e.g. <c>click</c> or <c>tabs.open</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>What happened in plain words, e.g. <c>Clicked “Save”</c>.</summary>
    public required string Summary { get; init; }

    /// <summary>The operation and how long it took, e.g. <c>click @e2 · 170 ms</c>.</summary>
    public required string Detail { get; init; }

    public bool Ok { get; init; } = true;
    public string? Error { get; init; }
    public string? TabId { get; init; }
    public string? Url { get; init; }
    public string? Title { get; init; }

    /// <summary>Where the element acted on was on the tab's 1280×800 screen.</summary>
    public BrowserBox? Box { get; init; }

    /// <summary>A screenshot the step took, which the conversation shows.</summary>
    public BrowserShot? Screenshot { get; init; }
}

public sealed record BrowserBox(double X, double Y, double Width, double Height);

/// <summary>A screenshot kept under <see cref="BrowserStep.SessionId"/>.</summary>
public sealed record BrowserShot(string Id, int Width, int Height);
