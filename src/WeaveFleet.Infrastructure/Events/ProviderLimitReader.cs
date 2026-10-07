using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Infrastructure.Events;

/// <summary>
/// Works out whether a model provider's limit stopped a turn (<see cref="TurnErrorKinds"/>), and when the provider said
/// a request can go again, from whatever the harness passed on: the error's name, HTTP status, response headers, body
/// and text.
/// </summary>
/// <remarks>
/// <para>Harnesses pass on different amounts. OpenCode keeps the provider's status, headers and body; OpenCode 2 a
/// typed name (<c>RateLimit</c>, <c>QuotaExceeded</c>) and the status; Pi only the text. So the reader takes what it's
/// given and falls back to the text, which is where subscription limits say when they reset ("You've hit your session
/// limit · resets 3:43am (UTC)", "Try again in 2 hours 5 minutes").</para>
/// <para>Limits that don't pass by waiting (out of credits, a spend cap, billing) aren't limits here: trying again
/// can't help.</para>
/// </remarks>
internal static partial class ProviderLimitReader
{
    /// <summary>The furthest ahead a reset is believed: a weekly limit, with a day to spare.</summary>
    internal static readonly TimeSpan LongestReset = TimeSpan.FromDays(8);

    /// <summary>Returns <paramref name="error"/> with its limit and retry time, when it's a limit and doesn't say already.</summary>
    public static TurnError Classify(
        TurnError error,
        DateTimeOffset now,
        int? status = null,
        JsonElement? headers = null,
        string? body = null)
    {
        var kind = TurnErrorKinds.IsKnown(error.Kind) ? error.Kind : ReadKind(error.Name, error.Message, status, body);
        if (kind is null)
            return error;

        var retryAt = error.RetryAt is { } said && IsBelievable(said, now)
            ? said
            : ReadRetryAt(headers, body, error.Message, now);
        return error with { Kind = kind, RetryAt = retryAt };
    }

    /// <summary>Which limit the failure is, or <see langword="null"/> when it isn't one a wait gets past.</summary>
    internal static string? ReadKind(string? name, string? message, int? status, string? body)
    {
        var text = $"{name}\n{message}\n{body}";
        if (NotResetting().IsMatch(text))
            return null;

        switch (name?.Trim().ToLowerInvariant())
        {
            case "ratelimit" or "ratelimiterror" or "rate_limit" or "rate_limit_error" or "ratelimitexceeded" or "too_many_requests":
                return UsageText().IsMatch(text) ? TurnErrorKinds.UsageLimit : TurnErrorKinds.RateLimit;
            case "quotaexceeded" or "usage_limit" or "usage_limit_reached" or "usagelimitexceeded" or "usagelimiterror"
                or "gousagelimiterror":
                return TurnErrorKinds.UsageLimit;
            case "overloaded" or "overloaded_error" or "serveroverloaded" or "server_is_overloaded":
                return TurnErrorKinds.Overloaded;
        }

        if (UsageText().IsMatch(text))
            return TurnErrorKinds.UsageLimit;
        if (status == 429 || RateText().IsMatch(text))
            return TurnErrorKinds.RateLimit;
        if (status is 529 or 503 || OverloadText().IsMatch(text))
            return TurnErrorKinds.Overloaded;
        return null;
    }

    /// <summary>When the provider said to try again: its headers, then its body, then its words.</summary>
    internal static DateTimeOffset? ReadRetryAt(JsonElement? headers, string? body, string? message, DateTimeOffset now)
    {
        var at = FromHeaders(headers, now) ?? FromBody(body, now) ?? FromText(message, now) ?? FromText(body, now);
        return at is { } value && IsBelievable(value, now) ? value : null;
    }

    private static bool IsBelievable(DateTimeOffset at, DateTimeOffset now) => at > now && at <= now + LongestReset;

    private static DateTimeOffset? FromHeaders(JsonElement? headers, DateTimeOffset now)
    {
        if (headers is not { ValueKind: JsonValueKind.Object } h)
            return null;

        string? Header(string name)
        {
            foreach (var property in h.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
            }
            return null;
        }

        if (double.TryParse(Header("retry-after-ms"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0)
            return now.AddMilliseconds(ms);

        // A subscription's window (Claude): when the limit that's in force resets.
        if (Header("anthropic-ratelimit-unified-status") is "rejected"
            && long.TryParse(Header("anthropic-ratelimit-unified-reset"), CultureInfo.InvariantCulture, out var unified))
            return DateTimeOffset.FromUnixTimeSeconds(unified);

        // ChatGPT's Codex windows: the later reset of the ones used up.
        DateTimeOffset? codex = null;
        foreach (var window in new[] { "primary", "secondary" })
        {
            if (double.TryParse(Header($"x-codex-{window}-used-percent"), NumberStyles.Float, CultureInfo.InvariantCulture, out var used) && used >= 100
                && double.TryParse(Header($"x-codex-{window}-reset-after-seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var after))
            {
                var at = now.AddSeconds(after);
                codex = codex is { } other && other > at ? other : at;
            }
        }
        if (codex is not null)
            return codex;

        if (Header("retry-after") is { } retryAfter)
        {
            if (double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                return now.AddSeconds(seconds);
            if (DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                return date;
        }

        // OpenAI's per-minute limits: "6m0s", "1.5s".
        DateTimeOffset? latest = null;
        foreach (var name in new[] { "x-ratelimit-reset-requests", "x-ratelimit-reset-tokens" })
        {
            if (ParseDuration(Header(name)) is { } wait)
            {
                var at = now + wait;
                latest = latest is { } other && other > at ? other : at;
            }
        }
        return latest;
    }

    /// <summary>A JSON error body: Codex's <c>resets_in_seconds</c> / <c>resets_at</c>, Gemini's <c>retryDelay</c>.</summary>
    private static DateTimeOffset? FromBody(string? body, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart() is not ['{', ..] and not ['[', ..])
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return Find(document.RootElement, now, depth: 0);
        }
        catch (JsonException)
        {
            return null;
        }

        static DateTimeOffset? Find(JsonElement element, DateTimeOffset now, int depth)
        {
            if (depth > 6)
                return null;

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (Find(item, now, depth + 1) is { } found)
                        return found;
                }
                return null;
            }

            if (element.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in element.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name.ToLowerInvariant())
                {
                    case "resets_in_seconds" or "reset_after_seconds" or "retry_after" or "retry_after_seconds" when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds) && seconds > 0:
                        return now.AddSeconds(seconds);
                    case "resets_at" or "reset_at" or "resetsat" when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch):
                        return epoch > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
                    case "resets_at" or "reset_at" or "resetsat" when value.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date):
                        return date;
                    case "retrydelay" when value.ValueKind == JsonValueKind.String && ParseDuration(value.GetString()) is { } wait:
                        return now + wait;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && Find(property.Value, now, depth + 1) is { } found)
                    return found;
            }
            return null;
        }
    }

    /// <summary>What the provider wrote: "try again in 2 hours", "resets 3:43am (UTC)", "Try again at Oct 9th, 2026 3:05 PM".</summary>
    internal static DateTimeOffset? FromText(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (WaitText().Match(text) is { Success: true } wait && ParseDuration(wait.Groups["duration"].Value) is { } duration)
            return now + duration;

        if (ClockText().Match(text) is { Success: true } clock)
            return ParseClock(clock, now);

        return null;
    }

    private static DateTimeOffset? ParseClock(Match match, DateTimeOffset now)
    {
        var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        if (match.Groups["ampm"].Success)
        {
            if (hour is < 1 or > 12)
                return null;
            var pm = match.Groups["ampm"].Value.StartsWith('p') || match.Groups["ampm"].Value.StartsWith('P');
            hour = hour % 12 + (pm ? 12 : 0);
        }
        if (hour > 23 || minute > 59)
            return null;

        // The CLI writes the time where it runs, which is Fleet's machine, unless it names the zone.
        var zone = TimeZoneInfo.Local;
        if (match.Groups["zone"].Success)
        {
            var name = match.Groups["zone"].Value;
            if (name is "UTC" or "GMT" or "Z")
                zone = TimeZoneInfo.Utc;
            else if (TimeZoneInfo.TryFindSystemTimeZoneById(name, out var found))
                zone = found;
            else
                return null;
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        DateTime day;
        if (match.Groups["month"].Success)
        {
            var month = DateTime.ParseExact(match.Groups["month"].Value[..3], "MMM", CultureInfo.InvariantCulture).Month;
            var dayOfMonth = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
            var year = match.Groups["year"].Success ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture) : local.Year;
            if (dayOfMonth > DateTime.DaysInMonth(year, month))
                return null;
            day = new DateTime(year, month, dayOfMonth, 0, 0, 0, DateTimeKind.Unspecified);
            if (!match.Groups["year"].Success && day < local.Date.AddDays(-1))
                day = day.AddYears(1);
        }
        else
        {
            day = local.Date;
        }

        var wall = day.AddHours(hour).AddMinutes(minute);
        var at = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        // A time without a date that's already gone is tomorrow's.
        if (!match.Groups["month"].Success && at <= now)
            at = at.AddDays(1);
        return at;
    }

    /// <summary>"1.5s", "6m0s", "2h 5m", "20 minutes", "1 hour and 5 minutes", "250ms".</summary>
    internal static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var total = TimeSpan.Zero;
        var any = false;
        foreach (Match part in DurationPart().Matches(text))
        {
            var value = double.Parse(part.Groups["value"].Value, CultureInfo.InvariantCulture);
            var unit = part.Groups["unit"].Value.ToLowerInvariant();
            total += unit switch
            {
                "ms" or "millisecond" or "milliseconds" => TimeSpan.FromMilliseconds(value),
                "s" or "sec" or "secs" or "second" or "seconds" => TimeSpan.FromSeconds(value),
                "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(value),
                "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(value),
                _ => TimeSpan.FromDays(value),
            };
            any = true;
        }
        return any && total > TimeSpan.Zero ? total : null;
    }

    // Limits a wait doesn't get past: credits, money, plans. "Not your usage limit" is Claude's word for a rate limit.
    [GeneratedRegex(@"insufficient[_\s-]?quota|credit balance|out of (?:extra )?usage|usage credits|spend(?:ing)? limit|budget|add funds|billing|payment|purchase|subscribe to|upgrade your plan|seat type|disabled by your admin|set to \$0", RegexOptions.IgnoreCase)]
    private static partial Regex NotResetting();

    [GeneratedRegex(@"(?<!not your )usage[_\s-]?limit|usagelimitexceeded|(?:hit|reached) your [\w\s-]{0,30}?limit|quota[_\s-]?exceeded|exceeded your (?:current )?quota", RegexOptions.IgnoreCase)]
    private static partial Regex UsageText();

    [GeneratedRegex(@"rate[_\s-]?limit|too[_\s-]?many[_\s-]?requests|throttl|temporarily limiting requests|resource[_\s-]?exhausted", RegexOptions.IgnoreCase)]
    private static partial Regex RateText();

    [GeneratedRegex(@"overloaded|\b529\b|service unavailable|temporarily unavailable|at capacity|server is busy", RegexOptions.IgnoreCase)]
    private static partial Regex OverloadText();

    [GeneratedRegex(@"(?:try again|retry|resets?|reset|available again|wait)\s+(?:in|after|for)\s+(?<duration>(?:\d+(?:\.\d+)?\s*[a-z]+(?:\s*(?:,|and)?\s*)?)+)", RegexOptions.IgnoreCase)]
    private static partial Regex WaitText();

    [GeneratedRegex(@"(?:resets?|reset at|try again at|available again at)\s+(?:at\s+|on\s+)?(?:(?<month>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s*(?:(?<year>\d{4}),?\s*)?(?:at\s+)?)?(?<hour>\d{1,2})(?:(?::(?<minute>\d{2}))\s*(?<ampm>[ap]\.?m\.?)?|\s*(?<ampm>[ap]\.?m\.?))(?:\s*\((?<zone>[A-Za-z_]+(?:/[A-Za-z_+\-]+)*)\))?", RegexOptions.IgnoreCase)]
    private static partial Regex ClockText();

    [GeneratedRegex(@"(?<value>\d+(?:\.\d+)?)\s*(?<unit>milliseconds?|ms|seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h|days?|d)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPart();
}
