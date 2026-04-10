using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Harnesses;

/// <summary>Options for resuming an existing harness session.</summary>
public sealed record HarnessResumeOptions
{
    public required string SessionId { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string ResumeToken { get; init; }
    public required string OwnerUserId { get; init; }
    public string? ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>Options for spawning a new harness instance.</summary>
public sealed record HarnessSpawnOptions
{
    public required string SessionId { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string OwnerUserId { get; init; }
    public string? InitialPrompt { get; init; }
    public string? Branch { get; init; }
    public string? ProjectId { get; init; }
    public string? ProjectName { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>
/// API-facing DTO returned by GET /api/harnesses.
/// Combines harness metadata with runtime availability.
/// </summary>
public sealed record HarnessInfo(
    string Type,
    string DisplayName,
    bool Available,
    string? Reason,
    HarnessCapabilities Capabilities);

/// <summary>An agent persona exposed by a harness.</summary>
public sealed record HarnessAgent(string Name, string? Description, string? Mode);

/// <summary>An AI provider supported by a harness.</summary>
public sealed record HarnessProvider(string Id, string Name, IReadOnlyList<HarnessModel> Models);

/// <summary>An AI model within a provider.</summary>
public sealed record HarnessModel(string Id, string Name);
