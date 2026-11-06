using System.Text.Json;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// This machine's MagicDNS name (<c>tailscale status --json</c> → <c>Self.DNSName</c>, trailing dot dropped), so a link
/// can say <c>falcon.tail9c2e.ts.net</c> instead of a 100.x address. Null when Tailscale isn't there or has no name.
/// The answer is kept for a minute: it's asked for on every pairing link.
/// </summary>
public sealed class TailnetName(ITailscale tailscale, TimeProvider? clock = null)
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private (string? Name, DateTimeOffset ReadAt)? _read;

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        if (_read is { } read && _clock.GetUtcNow() - read.ReadAt <= Keep)
            return read.Name;

        // Two callers at once may both ask Tailscale; the answers are the same, so the last one wins.
        var name = Parse(await tailscale.StatusJsonAsync(cancellationToken));
        _read = (name, _clock.GetUtcNow());
        return name;
    }

    private static string? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Self", out var self) || self.ValueKind != JsonValueKind.Object
                || !self.TryGetProperty("DNSName", out var dns) || dns.ValueKind != JsonValueKind.String)
                return null;
            var name = dns.GetString()?.TrimEnd('.');
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
