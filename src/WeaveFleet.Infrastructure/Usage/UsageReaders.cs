using System.Globalization;
using System.Text.Json;
using WeaveFleet.Application.Usage;
using Microsoft.Data.Sqlite;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

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

    /// <summary>
    /// The databases where OpenCode 2 keeps sign-ins: <c>OPENCODE_DB</c> when Fleet's own environment sets it, Fleet's
    /// separate install's, and OpenCode's usual one. Only those that exist are read.
    /// </summary>
    public IReadOnlyList<string> OpenCode2Databases
    {
        get
        {
            var data = environment("XDG_DATA_HOME");
            var root = !string.IsNullOrWhiteSpace(data) && Path.IsPathRooted(data) ? data : Path.Combine(homeDirectory, ".local", "share");
            var paths = new List<string>();
            if (environment("OPENCODE_DB") is { Length: > 0 } custom)
                paths.Add(Path.IsPathRooted(custom) ? custom : Path.Combine(root, "opencode", custom));
            paths.Add(Path.Combine(OpenCode2Install.RootFor(homeDirectory), "data", "opencode.db"));
            paths.Add(Path.Combine(root, "opencode", "opencode.db"));
            return paths.Distinct(StringComparer.Ordinal).Where(File.Exists).ToList();
        }
    }

    public string AnthropicUrl => Base("FLEET_USAGE_ANTHROPIC_URL", "https://api.anthropic.com");
    public string OpenAiUrl => Base("FLEET_USAGE_OPENAI_URL", "https://chatgpt.com");
    public string GitHubUrl => Base("FLEET_USAGE_GITHUB_URL", "https://api.github.com");
    public string OpenRouterUrl => Base("FLEET_USAGE_OPENROUTER_URL", "https://openrouter.ai");

    private string Base(string name, string fallback) => (environment(name) is { Length: > 0 } value ? value : fallback).TrimEnd('/');
}

/// <summary>Reading a sign-in file and asking a provider, the same way for every reader.</summary>
internal sealed record SignIn(string Harness, string Source, string Type, string? Secret, string? AccountId, bool Expired);

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

    /// <summary>
    /// OpenCode 2's active sign-ins for <paramref name="integrations"/>, read from its <c>credential</c> table with the
    /// database opened read-only. Anything unreadable (locked, garbled, no such table) is no sign-in.
    /// </summary>
    public static IEnumerable<SignIn> OpenCode2SignIns(UsageSources sources, TimeProvider time, params string[] integrations)
    {
        foreach (var path in sources.OpenCode2Databases)
        {
            var rows = new List<string>();
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM credential WHERE active = 1 AND lower(integration_id) IN (" + string.Join(",", integrations.Select((_, i) => "$p" + i)) + ")";
                for (var i = 0; i < integrations.Length; i++)
                    command.Parameters.AddWithValue("$p" + i, integrations[i]);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add(reader.GetString(0));
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var row in rows)
            {
                SignIn? found = null;
                try
                {
                    using var value = JsonDocument.Parse(row);
                    var root = value.RootElement;
                    var type = Text(root, "type");
                    var secret = type == "oauth" ? Text(root, "access") : Text(root, "key");
                    var expires = Number(root, "expires");
                    var account = root.TryGetProperty("metadata", out var metadata) ? Text(metadata, "accountID") : null;
                    if (type is not null)
                        found = new SignIn("opencode2", "OpenCode 2", type, secret, account, expires is { } ms && ms <= time.GetUtcNow().ToUnixTimeMilliseconds());
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                }

                if (found is not null)
                    yield return found;
            }
        }
    }

    /// <summary>
    /// The sign-ins one provider has across both OpenCodes: the one to read (an unexpired one first) and the harnesses
    /// that have any.
    /// </summary>
    private static readonly string[] OpenCodeHarnesses = ["opencode", "opencode2"];

    public static (SignIn? Chosen, IReadOnlyList<string> Harnesses) Choose(IEnumerable<SignIn> signIns)
    {
        var all = signIns.ToList();
        var harnesses = OpenCodeHarnesses.Where(h => all.Any(s => s.Harness == h)).ToList();
        return (all.FirstOrDefault(s => !s.Expired) ?? all.FirstOrDefault(), harnesses);
    }

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
