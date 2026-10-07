using System.Globalization;
using System.Text.Json;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Infrastructure.Events;

/// <summary>
/// Reads the failure out of a harness error payload into Fleet's <see cref="TurnError"/>.
/// </summary>
/// <remarks>
/// Harnesses disagree about where the text lives. OpenCode nests it twice —
/// <c>{ "error": { "name": "APIError", "data": { "message": "…", "isRetryable": true } } }</c> — while Pi
/// sends <c>{ "message": "…" }</c> flat, and some OpenCode errors carry only a name. The reader accepts the
/// union and falls back rather than dropping the failure: a failure shown badly still beats silence, which
/// is what a dropped one looks like.
/// </remarks>
internal static class HarnessErrorReader
{
    /// <summary>
    /// Reads the failure from a <c>session.error</c> payload, looking under <c>error</c> first and then at
    /// the payload itself. Returns <see langword="null"/> when there is nothing usable to show.
    /// </summary>
    public static TurnError? TryReadFromPayload(JsonElement? payload, DateTimeOffset? now = null)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p)
            return null;

        if (p.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object)
            return TryRead(nested, now);

        return TryRead(p, now);
    }

    /// <summary>
    /// Reads an error object itself (a harness message's <c>error</c> property, or the <c>error</c> of a
    /// <c>session.error</c> payload).
    /// </summary>
    /// <remarks>
    /// A model provider's limit is read from it too (<see cref="ProviderLimitReader"/>): OpenCode keeps the provider's
    /// <c>statusCode</c>, <c>responseHeaders</c> and <c>responseBody</c> under <c>data</c>, OpenCode 2 a <c>status</c>,
    /// and an adapter that knows better says <c>kind</c> and <c>retryAt</c> itself.
    /// </remarks>
    public static TurnError? TryRead(JsonElement? element, DateTimeOffset? now = null)
    {
        if (element is not { ValueKind: JsonValueKind.Object } error)
            return null;

        var data = error.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
            ? d
            : (JsonElement?)null;

        var name = ReadString(error, "name");
        var message = ReadString(data, "message") ?? ReadString(error, "message");

        if (name is null && message is null)
            return null;

        var turnError = new TurnError
        {
            Name = name ?? "Error",
            Message = message ?? name!,
            IsRetryable = ReadBool(data, "isRetryable") ?? ReadBool(error, "isRetryable") ?? false,
            Kind = ReadString(error, "kind"),
            RetryAt = ReadString(error, "retryAt") is { } retryAt
                && DateTimeOffset.TryParse(retryAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? at
                    : null,
        };

        return ProviderLimitReader.Classify(
            turnError,
            now ?? DateTimeOffset.UtcNow,
            status: ReadInt(data, "statusCode") ?? ReadInt(error, "statusCode") ?? ReadInt(error, "status"),
            headers: data is { } d2 && d2.TryGetProperty("responseHeaders", out var headers) ? headers : null,
            body: ReadString(data, "responseBody"));
    }

    private static int? ReadInt(JsonElement? element, string property)
        => element is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;

    private static string? ReadString(JsonElement? element, string property)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj)
            return null;

        return obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;
    }

    private static bool? ReadBool(JsonElement? element, string property)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
