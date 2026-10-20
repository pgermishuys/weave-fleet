using System.Globalization;
using System.Text.Json;
using WeaveFleet.Application.Usage;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Acp;

namespace WeaveFleet.Infrastructure.Usage;

/// <summary>
/// GitHub Copilot's monthly quotas (premium requests and the like). Read through weave-copilot's <c>_fleet/usage</c> when the
/// Copilot harness is signed in, else, for Pi, straight from GitHub with the GitHub token Fleet keeps for it. One provider
/// entry either way. Copilot has no live feed: polled only. The token never leaves this class.
/// </summary>
internal sealed class CopilotUsageProvider(
    IHttpClientFactory http,
    UsageSources sources,
    Func<CancellationToken, Task<bool>> copilotReady,
    Func<CancellationToken, Task<JsonElement>> askCopilot,
    Func<CancellationToken, Task<string?>> githubToken) : IUsageProvider
{
    private const int MethodNotFound = -32601;

    public string Provider => "copilot";
    public string Name => "GitHub Copilot";
    public string Source => "weave-copilot";
    public IReadOnlyList<string> Harnesses { get; } = ["copilot", "pi"];
    public bool NeedsConsent => true;

    public async Task<bool> HasSignInAsync(CancellationToken ct)
        => await copilotReady(ct).ConfigureAwait(false) || await githubToken(ct).ConfigureAwait(false) is not null;

    public async Task<UsageReading> ReadAsync(CancellationToken ct)
    {
        if (await copilotReady(ct).ConfigureAwait(false))
            return await ReadThroughCopilotAsync(ct).ConfigureAwait(false);

        if (await githubToken(ct).ConfigureAwait(false) is not { } token)
            return new UsageReading(UsageStates.SignedOut);

        var (body, failure) = await UsageRead.GetAsync(http, sources.GitHubUrl + "/copilot_internal/user", token, request =>
        {
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("User-Agent", "weave-fleet");
        }, ct).ConfigureAwait(false);
        if (failure is not null)
            return failure;

        using (body)
            return FromGitHub(body!.RootElement);
    }

    private async Task<UsageReading> ReadThroughCopilotAsync(CancellationToken ct)
    {
        JsonElement reply;
        try
        {
            reply = await askCopilot(ct).ConfigureAwait(false);
        }
        // An older weave-copilot doesn't know the request (method not found); anything else it says is just as unreadable.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UsageReading(UsageStates.Unreadable) { BackOff = ex is AcpException { Code: MethodNotFound } };
        }

        if (reply.ValueKind != JsonValueKind.Object)
            return new UsageReading(UsageStates.Unreadable);
        if (!reply.TryGetProperty("authenticated", out var yes) || yes.ValueKind != JsonValueKind.True)
            return new UsageReading(UsageStates.SignedOut);

        var quotas = new List<Quota>();
        if (reply.TryGetProperty("quotas", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in list.EnumerateArray())
                if (UsageRead.Text(q, "id") is { } id)
                    quotas.Add(new Quota(id, UsageRead.Number(q, "entitlement"), UsageRead.Number(q, "remaining"), UsageRead.Number(q, "percentRemaining"), q.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True));
        }

        return Build(UsageRead.Text(reply, "plan") ?? UsageRead.Text(reply, "login"), ParseDate(UsageRead.Text(reply, "resetAt")), quotas) with { AccountId = UsageRead.Text(reply, "login") };
    }

    /// <summary>GitHub's own answer: <c>{ login, copilot_plan, quota_reset_date, quota_snapshots: { premium_interactions: { entitlement, remaining, percent_remaining, unlimited }, … } }</c>.</summary>
    private static UsageReading FromGitHub(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("quota_snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Object)
            return new UsageReading(UsageStates.Unreadable);

        var quotas = new List<Quota>();
        foreach (var snapshot in snapshots.EnumerateObject())
        {
            if (snapshot.Value.ValueKind != JsonValueKind.Object)
                continue;
            var q = snapshot.Value;
            quotas.Add(new Quota(UsageRead.Text(q, "quota_id") ?? snapshot.Name, UsageRead.Number(q, "entitlement"), UsageRead.Number(q, "remaining"),
                UsageRead.Number(q, "percent_remaining"), q.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True));
        }

        return Build(UsageRead.Text(root, "copilot_plan") ?? UsageRead.Text(root, "login"),
            ParseDate(UsageRead.Text(root, "quota_reset_date_utc") ?? UsageRead.Text(root, "quota_reset_date")), quotas) with { AccountId = UsageRead.Text(root, "login") };
    }

    private static UsageReading Build(string? account, DateTimeOffset? resetsAt, List<Quota> quotas)
    {
        var windows = new List<UsageLimitWindow>();
        foreach (var quota in quotas)
        {
            if (quota.Unlimited)
                continue;
            double? used = quota.Entitlement is > 0 && quota.Remaining is { } left ? 1 - left / quota.Entitlement
                : quota.PercentRemaining is { } percent ? 1 - percent / 100 : null;
            if (used is not { } value)
                continue;
            value = Math.Clamp(value, 0, 1);
            windows.Add(new UsageLimitWindow
            {
                Window = quota.Id,
                Utilization = value,
                ResetsAt = resetsAt,
                // The calendar month that ends at the reset.
                WindowSeconds = resetsAt is { } end ? (int)(end - end.AddMonths(-1)).TotalSeconds : null,
                Status = UsageRead.StatusFor(value),
            });
        }

        return windows.Count == 0 ? new UsageReading(UsageStates.Unreadable) : new UsageReading(UsageStates.Ok, account, windows);
    }

    private static DateTimeOffset? ParseDate(string? text)
        => text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    private sealed record Quota(string Id, double? Entitlement, double? Remaining, double? PercentRemaining, bool Unlimited);
}
