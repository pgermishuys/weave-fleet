using System.Text.Json.Serialization;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Usage;

/// <summary>The states a provider's usage can be in (<c>state</c> on the wire).</summary>
public static class UsageStates
{
    /// <summary>Read; the windows or credits are the provider's answer.</summary>
    public const string Ok = "ok";

    /// <summary>A sign-in was found, but the user hasn't pressed Allow.</summary>
    public const string NotAllowed = "not_allowed";

    /// <summary>Pay-per-token or an API key: there's no limit window to show.</summary>
    public const string NoWindow = "no_window";

    /// <summary>Fleet couldn't read it right now: the network, an answer it didn't know, a 5xx or a 429.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>The provider turned the sign-in down (401, or an expired token): sign in again.</summary>
    public const string SignedOut = "signed_out";

    /// <summary>Fleet can't read this sign-in yet (OpenCode 2).</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>What a provider's account has spent of what it may (OpenRouter's key limit).</summary>
public sealed record UsageCredits(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Remaining,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Limit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Used);

/// <summary>What a reader found, before Fleet adds the provider's name and when it asked.</summary>
public sealed record UsageReading(
    string State,
    string? Account = null,
    IReadOnlyList<UsageLimitWindow>? Windows = null,
    UsageCredits? Credits = null)
{
    /// <summary>True for a 429 or a 401: Fleet asks this provider less often for a while.</summary>
    public bool BackOff { get; init; }

    /// <summary>The account's raw id when only the answer knows it (Copilot's login). Never sent: the service hashes it into <c>accountKey</c>.</summary>
    public string? AccountId { get; init; }
}

/// <summary>The opaque key two Fleets compare to tell they hold the same account. Derived from the account id only, never a token or key.</summary>
public static class AccountKey
{
    public static string? For(string provider, string? accountId)
        => string.IsNullOrWhiteSpace(accountId) ? null
            : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provider + ":" + accountId)))[..16];
}

/// <summary>One provider's usage as clients get it.</summary>
public sealed record ProviderUsage(
    string Provider,
    string Name,
    string State,
    IReadOnlyList<string> Harnesses,
    IReadOnlyList<UsageLimitWindow> Windows,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Account,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ReadAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] UsageCredits? Credits,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AccountKey = null);

/// <summary>The body of <c>GET /api/usage/limits</c> and of the <c>usage.limits</c> event.</summary>
public sealed record UsageLimitsSnapshot(bool Enabled, IReadOnlyList<ProviderUsage> Providers);
