namespace WeaveFleet.Application.Harnesses;

/// <summary>
/// Tests connectivity for the NuCode harness using the current user's configured credentials and preferences.
/// </summary>
public interface INuCodeConnectionTester
{
    /// <summary>
    /// Attempts a minimal completion request using the current NuCode configuration.
    /// </summary>
    /// <returns>A result indicating success or failure with latency information.</returns>
    Task<NuCodeConnectionTestResult> TestAsync(CancellationToken ct);

    /// <summary>
    /// Attempts a minimal completion request for a specific provider.
    /// Uses the provider's stored credentials and the user's model preference.
    /// </summary>
    /// <param name="providerId">The provider to test.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result indicating success or failure with latency information.</returns>
    Task<NuCodeConnectionTestResult> TestAsync(string providerId, CancellationToken ct);
}

/// <summary>Result of a NuCode connection test.</summary>
/// <param name="Success">Whether the connection test succeeded.</param>
/// <param name="Error">Error message when <see cref="Success"/> is false.</param>
/// <param name="LatencyMs">Elapsed milliseconds for the round-trip.</param>
public sealed record NuCodeConnectionTestResult(bool Success, string? Error, int LatencyMs);
