using System.Globalization;
using System.Text.Json;
using WeaveFleet.Application.Usage;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Usage;

/// <summary>Where the readers look and who they ask. The base addresses can be set so a scratch Fleet asks a fake API.</summary>
internal sealed class UsageSources(string homeDirectory, Func<string, string?> environment)
{
    /// <summary>Name of the HTTP client the readers use.</summary>
    public const string HttpClientName = "UsageLimits";

    public static UsageSources FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable);

    public string ClaudeCredentialsFile => Path.Combine(homeDirectory, ".claude", ".credentials.json");

    /// <summary>OpenCode 1's sign-in file, in <c>XDG_DATA_HOME</c> when that's set.</summary>
    public string OpenCodeAuthFile
    {
        get
        {
            var data = environment("XDG_DATA_HOME");
            var root = !string.IsNullOrWhiteSpace(data) && Path.IsPathRooted(data) ? data : Path.Combine(homeDirectory, ".local", "share");
            return Path.Combine(root, "opencode", "auth.json");
        }
    }

    public string AnthropicUrl => Base("FLEET_USAGE_ANTHROPIC_URL", "https://api.anthropic.com");
    public string OpenAiUrl => Base("FLEET_USAGE_OPENAI_URL", "https://chatgpt.com");
    public string OpenRouterUrl => Base("FLEET_USAGE_OPENROUTER_URL", "https://openrouter.ai");

    private string Base(string name, string fallback) => (environment(name) is { Length: > 0 } value ? value : fallback).TrimEnd('/');
}

/// <summary>Reading a sign-in file and asking a provider, the same way for every reader.</summary>
internal static class UsageRead
{
    /// <summary>The file's JSON, or null when it's missing or isn't JSON.</summary>
    public static JsonDocument? ReadJsonFile(string path)
    {
        try
        {
            return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static string? Text(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    public static double? Number(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    /// <summary>OpenCode's entry for a provider in <c>auth.json</c>, or null.</summary>
    public static JsonElement? AuthEntry(JsonDocument? auth, string provider)
        => auth is not null && auth.RootElement.ValueKind == JsonValueKind.Object
            && auth.RootElement.TryGetProperty(provider, out var entry) && entry.ValueKind == JsonValueKind.Object ? entry : null;

    /// <summary>Where a window with <paramref name="used"/> (0 to 1) stands.</summary>
    public static string StatusFor(double used) => used >= 1 ? UsageLimitStatuses.Rejected : used >= 0.8 ? UsageLimitStatuses.Warning : UsageLimitStatuses.Allowed;

    /// <summary>
    /// GETs <paramref name="url"/>. Returns the answer's JSON, or a reading to give back instead: a 401 or 403 is signed
    /// out, a 429 is unreadable and asks Fleet to back off, anything else that isn't JSON is unreadable.
    /// </summary>
    public static async Task<(JsonDocument? Body, UsageReading? Failure)> GetAsync(
        IHttpClientFactory http, string url, string token, Action<HttpRequestMessage> extraHeaders, CancellationToken ct)
    {
        try
        {
            using var client = http.CreateClient(UsageSources.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            extraHeaders(request);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 401 or 403)
                return (null, new UsageReading(UsageStates.SignedOut) { BackOff = true });
            if (status == 429)
                return (null, new UsageReading(UsageStates.Unreadable) { BackOff = true });
            if (!response.IsSuccessStatusCode)
                return (null, new UsageReading(UsageStates.Unreadable));
            return (await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException && !ct.IsCancellationRequested)
        {
            return (null, new UsageReading(UsageStates.Unreadable));
        }
    }

    public static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
