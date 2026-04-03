namespace WeaveFleet.Application.Harnesses;

/// <summary>
/// DI-based discovery of registered harnesses.
/// Powers GET /api/harnesses.
/// </summary>
public interface IHarnessRegistry
{
    /// <summary>All registered harnesses.</summary>
    IReadOnlyList<IHarness> GetAll();

    /// <summary>Find a harness by its type identifier, or null if not registered.</summary>
    IHarness? GetByType(string harnessType);

    /// <summary>Check availability of all harnesses (for API response).</summary>
    Task<IReadOnlyList<HarnessInfo>> GetAvailabilityAsync(CancellationToken ct);
}
