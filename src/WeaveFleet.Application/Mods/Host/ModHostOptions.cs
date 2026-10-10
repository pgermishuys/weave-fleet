namespace WeaveFleet.Application.Mods.Host;

/// <summary>The mod host's timings. Tests pass shorter ones.</summary>
public sealed record ModHostOptions
{
    /// <summary>How long a request to the host may take before Fleet restarts it.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a host gets to exit after <c>shutdown</c> before it's killed.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The waits before restarting a host that died, one per crash in a row; the last repeats.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } = [.. new[] { 0.5, 1, 2, 4, 8, 16, 30 }.Select(TimeSpan.FromSeconds)];

    /// <summary>A host that stayed up this long starts the waits again from the first.</summary>
    public TimeSpan StableUptime { get; init; } = TimeSpan.FromSeconds(60);
}
