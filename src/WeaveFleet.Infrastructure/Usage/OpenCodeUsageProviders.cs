using System.Text.Json;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Usage;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Usage;

/// <summary>
/// Codex's (ChatGPT's) two windows, asked of ChatGPT with the sign-in OpenCode keeps in its <c>auth.json</c>. An API key
/// has no window to show.
/// </summary>
internal sealed class CodexUsageProvider(IHttpClientFactory http, UsageSources sources, TimeProvider time) : IUsageProvider
{
    public string Provider => "codex";
    public string Name => "Codex";
    public string Source => "OpenCode";
    public IReadOnlyList<string> Harnesses { get; } = ["opencode"];
    public bool NeedsConsent => true;

    public Task<bool> HasSignInAsync(CancellationToken ct)
    {
        using var auth = UsageRead.ReadJsonFile(sources.OpenCodeAuthFile);
        return Task.FromResult(UsageRead.AuthEntry(auth, "openai") is not null);
    }

    public async Task<UsageReading> ReadAsync(CancellationToken ct)
    {
        string token;
        string? accountId;
        using (var auth = UsageRead.ReadJsonFile(sources.OpenCodeAuthFile))
        {
            if (UsageRead.AuthEntry(auth, "openai") is not { } entry)
                return new UsageReading(UsageStates.SignedOut);
            if (UsageRead.Text(entry, "type") != "oauth")
                return new UsageReading(UsageStates.NoWindow);
            if (UsageRead.Text(entry, "access") is not { } access)
                return new UsageReading(UsageStates.SignedOut);
            token = access;
            accountId = UsageRead.Text(entry, "accountId");
        }

        var (body, failure) = await UsageRead.GetAsync(http, sources.OpenAiUrl + "/backend-api/wham/usage", token, request =>
        {
            if (accountId is not null)
                request.Headers.Add("ChatGPT-Account-Id", accountId);
        }, ct).ConfigureAwait(false);
        if (failure is not null)
            return failure;

        using (body)
        {
            var root = body!.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("rate_limit", out var limit) || limit.ValueKind != JsonValueKind.Object)
                return new UsageReading(UsageStates.Unreadable);

            var windows = new List<UsageLimitWindow>();
            foreach (var (json, name) in new[] { ("primary_window", "primary"), ("secondary_window", "secondary") })
            {
                if (!limit.TryGetProperty(json, out var window) || window.ValueKind != JsonValueKind.Object || UsageRead.Number(window, "used_percent") is not { } percent)
                    continue;
                var used = Math.Clamp(percent / 100, 0, 1);
                DateTimeOffset? resets = UsageRead.Number(window, "reset_at") is { } epoch ? DateTimeOffset.FromUnixTimeSeconds((long)epoch)
                    : UsageRead.Number(window, "reset_after_seconds") is { } after ? time.GetUtcNow().AddSeconds(after) : null;
                windows.Add(new UsageLimitWindow { Window = name, Utilization = used, ResetsAt = resets, Status = UsageRead.StatusFor(used) });
            }

            return windows.Count == 0 ? new UsageReading(UsageStates.Unreadable) : new UsageReading(UsageStates.Ok, UsageRead.Text(root, "plan_type"), windows);
        }
    }
}

/// <summary>What an OpenRouter key has used and may use, asked with the key OpenCode keeps in its <c>auth.json</c>. No windows.</summary>
internal sealed class OpenRouterUsageProvider(IHttpClientFactory http, UsageSources sources) : IUsageProvider
{
    public string Provider => "openrouter";
    public string Name => "OpenRouter";
    public string Source => "OpenCode";
    public IReadOnlyList<string> Harnesses { get; } = ["opencode"];
    public bool NeedsConsent => true;

    public Task<bool> HasSignInAsync(CancellationToken ct)
    {
        using var auth = UsageRead.ReadJsonFile(sources.OpenCodeAuthFile);
        return Task.FromResult(UsageRead.AuthEntry(auth, "openrouter") is { } entry && UsageRead.Text(entry, "key") is not null);
    }

    public async Task<UsageReading> ReadAsync(CancellationToken ct)
    {
        string key;
        using (var auth = UsageRead.ReadJsonFile(sources.OpenCodeAuthFile))
        {
            if (UsageRead.AuthEntry(auth, "openrouter") is not { } entry || UsageRead.Text(entry, "key") is not { } found)
                return new UsageReading(UsageStates.SignedOut);
            key = found;
        }

        var (body, failure) = await UsageRead.GetAsync(http, sources.OpenRouterUrl + "/api/v1/key", key, _ => { }, ct).ConfigureAwait(false);
        if (failure is not null)
            return failure;

        using (body)
        {
            if (body!.RootElement.ValueKind != JsonValueKind.Object || !body.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || UsageRead.Number(data, "usage") is null)
                return new UsageReading(UsageStates.Unreadable);
            return new UsageReading(UsageStates.Ok, Credits: new UsageCredits(UsageRead.Number(data, "limit_remaining"), UsageRead.Number(data, "limit"), UsageRead.Number(data, "usage")));
        }
    }
}

/// <summary>OpenCode 2 keeps its sign-in where Fleet can't read it yet: listed as unsupported while that harness is installed.</summary>
internal sealed class OpenCode2UsageProvider(HarnessAvailabilityCache availability) : IUsageProvider
{
    public string Provider => "opencode2";
    public string Name => "OpenCode 2";
    public string Source => "OpenCode 2";
    public IReadOnlyList<string> Harnesses { get; } = ["opencode2"];
    public bool NeedsConsent => false;

    public async Task<bool> HasSignInAsync(CancellationToken ct)
        => (await availability.GetAsync(false, ct).ConfigureAwait(false)).Harnesses.Any(h => h.Type == "opencode2" && h.Available);

    public Task<UsageReading> ReadAsync(CancellationToken ct) => Task.FromResult(new UsageReading(UsageStates.Unsupported));
}
