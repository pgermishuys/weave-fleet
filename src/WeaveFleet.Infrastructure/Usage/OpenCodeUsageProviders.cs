using System.Text.Json;
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
    public string Source => Choose().Chosen?.Source ?? "OpenCode";
    public IReadOnlyList<string> Harnesses => Choose().Harnesses is { Count: > 0 } found ? found : ["opencode"];
    public bool NeedsConsent => true;
    public string? AccountId => Choose().Chosen?.AccountId;

    /// <summary>OpenCode's sign-in, then OpenCode 2's (ids as OpenCode 2 might name ChatGPT's).</summary>
    private (SignIn? Chosen, IReadOnlyList<string> Harnesses) Choose()
    {
        using var auth = UsageRead.ReadJsonFile(sources.OpenCodeAuthFile);
        var signIns = new List<SignIn>();
        if (UsageRead.AuthEntry(auth, "openai") is { } entry)
            signIns.Add(new SignIn("opencode", "OpenCode", UsageRead.Text(entry, "type") ?? "", UsageRead.Text(entry, "access"), UsageRead.Text(entry, "accountId"), false));
        signIns.AddRange(UsageRead.OpenCode2SignIns(sources, time, "openai", "chatgpt", "codex"));
        return UsageRead.Choose(signIns);
    }

    public Task<bool> HasSignInAsync(CancellationToken ct) => Task.FromResult(Choose().Chosen is not null);

    public async Task<UsageReading> ReadAsync(CancellationToken ct)
    {
        if (Choose().Chosen is not { } signIn)
            return new UsageReading(UsageStates.SignedOut);
        if (signIn.Type != "oauth")
            return new UsageReading(UsageStates.NoWindow);
        // OpenCode 2 renews a token when it uses it: an old one is not a sign-out.
        if (signIn.Expired)
            return new UsageReading(UsageStates.Unreadable);
        if (signIn.Secret is not { } token)
            return new UsageReading(UsageStates.SignedOut);
        var accountId = signIn.AccountId;

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
                windows.Add(new UsageLimitWindow { Window = name, Utilization = used, ResetsAt = resets, WindowSeconds = UsageRead.Number(window, "limit_window_seconds") is { } seconds and > 0 ? (int)seconds : null, Status = UsageRead.StatusFor(used) });
            }

            return windows.Count == 0 ? new UsageReading(UsageStates.Unreadable) : new UsageReading(UsageStates.Ok, UsageRead.Text(root, "plan_type"), windows);
        }
    }
}

/// <summary>What an OpenRouter key has used and may use, asked with the key OpenCode keeps in its <c>auth.json</c>. No windows.</summary>
internal sealed class OpenRouterUsageProvider(IHttpClientFactory http, UsageSources sources, TimeProvider time) : IUsageProvider
{
    public string Provider => "openrouter";
    public string Name => "OpenRouter";
    public string Source => Choose().Chosen?.Source ?? "OpenCode";
    public IReadOnlyList<string> Harnesses => Choose().Harnesses is { Count: > 0 } found ? found : ["opencode"];
    public bool NeedsConsent => true;

    private (SignIn? Chosen, IReadOnlyList<string> Harnesses) Choose()
    {
        using var auth = UsageRead.ReadJsonFile(sources.OpenCodeAuthFile);
        var signIns = new List<SignIn>();
        if (UsageRead.AuthEntry(auth, "openrouter") is { } entry && UsageRead.Text(entry, "key") is { } key)
            signIns.Add(new SignIn("opencode", "OpenCode", "key", key, null, false));
        signIns.AddRange(UsageRead.OpenCode2SignIns(sources, time, "openrouter").Where(s => s.Type == "key" && s.Secret is not null));
        return UsageRead.Choose(signIns);
    }

    public Task<bool> HasSignInAsync(CancellationToken ct) => Task.FromResult(Choose().Chosen is not null);

    public async Task<UsageReading> ReadAsync(CancellationToken ct)
    {
        if (Choose().Chosen?.Secret is not { } key)
            return new UsageReading(UsageStates.SignedOut);

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
