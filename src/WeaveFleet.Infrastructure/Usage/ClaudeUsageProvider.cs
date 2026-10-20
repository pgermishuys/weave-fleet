using System.Text.Json;
using WeaveFleet.Application.Usage;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Usage;

/// <summary>
/// Claude's five-hour and weekly windows, asked of Anthropic with the token Claude Code keeps in
/// <c>~/.claude/.credentials.json</c>. Read only: an expired token is signed out, Fleet never refreshes it.
/// </summary>
internal sealed class ClaudeUsageProvider(IHttpClientFactory http, UsageSources sources, TimeProvider time) : IUsageProvider
{
    private static readonly string[] Windows = ["five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet"];

    public string Provider => "claude";
    public string Name => "Claude";
    public string Source => "Claude Code";
    public IReadOnlyList<string> Harnesses { get; } = ["claude-code"];
    public bool NeedsConsent => false;

    /// <summary>The credentials file has no stable account id today; use one only if Claude Code ever writes it (<c>accountUuid</c>). Never the token.</summary>
    public string? AccountId
    {
        get
        {
            using var file = UsageRead.ReadJsonFile(sources.ClaudeCredentialsFile);
            return file is null ? null : UsageRead.Text(file.RootElement, "accountUuid") ?? (OAuth(file.RootElement) is { } oauth ? UsageRead.Text(oauth, "accountUuid") : null);
        }
    }

    public Task<bool> HasSignInAsync(CancellationToken ct)
    {
        using var file = UsageRead.ReadJsonFile(sources.ClaudeCredentialsFile);
        return Task.FromResult(file is not null && OAuth(file.RootElement) is { } oauth && UsageRead.Text(oauth, "accessToken") is not null);
    }

    public async Task<UsageReading> ReadAsync(CancellationToken ct)
    {
        string token;
        string? plan;
        using (var file = UsageRead.ReadJsonFile(sources.ClaudeCredentialsFile))
        {
            if (file is null || OAuth(file.RootElement) is not { } oauth || UsageRead.Text(oauth, "accessToken") is not { } found)
                return new UsageReading(UsageStates.SignedOut);
            // expiresAt is milliseconds since the epoch.
            if (UsageRead.Number(oauth, "expiresAt") is { } expires && DateTimeOffset.FromUnixTimeMilliseconds((long)expires) <= time.GetUtcNow())
                return new UsageReading(UsageStates.SignedOut);
            token = found;
            plan = UsageRead.Text(oauth, "subscriptionType");
        }

        var (body, failure) = await UsageRead.GetAsync(http, sources.AnthropicUrl + "/api/oauth/usage", token,
            request => request.Headers.Add("anthropic-beta", "oauth-2025-04-20"), ct).ConfigureAwait(false);
        if (failure is not null)
            return failure;

        using (body)
        {
            if (body!.RootElement.ValueKind != JsonValueKind.Object)
                return new UsageReading(UsageStates.Unreadable);

            var windows = new List<UsageLimitWindow>();
            foreach (var name in Windows)
            {
                if (!body.RootElement.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object || UsageRead.Number(window, "utilization") is not { } percent)
                    continue;
                var used = Math.Clamp(percent / 100, 0, 1);
                DateTimeOffset? resets = UsageRead.Text(window, "resets_at") is { } text && DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;
                windows.Add(new UsageLimitWindow { Window = name, Utilization = used, ResetsAt = resets, WindowSeconds = name == "five_hour" ? 18000 : 604800, Status = UsageRead.StatusFor(used) });
            }

            return windows.Count == 0 ? new UsageReading(UsageStates.Unreadable) : new UsageReading(UsageStates.Ok, plan, windows);
        }
    }

    private static JsonElement? OAuth(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("claudeAiOauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object ? oauth : null;
}
