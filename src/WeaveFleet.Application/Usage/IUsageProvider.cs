namespace WeaveFleet.Application.Usage;

/// <summary>
/// One provider account's usage, read from the sign-in already on this computer. Keyed by provider, not harness: Codex
/// is the same account whichever harness signed in. Readers only read a sign-in, never change or refresh it.
/// </summary>
public interface IUsageProvider
{
    /// <summary>The id on the wire: <c>claude</c>, <c>codex</c>, <c>openrouter</c>, <c>opencode2</c>.</summary>
    string Provider { get; }

    /// <summary>What the user knows it as.</summary>
    string Name { get; }

    /// <summary>Where the sign-in is read from: <c>Claude Code</c>, <c>OpenCode</c>.</summary>
    string Source { get; }

    /// <summary>The harness types that use this account; their busy sessions make Fleet ask.</summary>
    IReadOnlyList<string> Harnesses { get; }

    /// <summary>False when the user needn't press Allow before Fleet asks (Claude: Claude Code's own sign-in).</summary>
    bool NeedsConsent { get; }

    /// <summary>Whether a sign-in is there. Looks at the files only: no network, no token kept.</summary>
    Task<bool> HasSignInAsync(CancellationToken ct);

    /// <summary>Asks the provider. Never throws for a bad answer: that's a state.</summary>
    Task<UsageReading> ReadAsync(CancellationToken ct);
}
