using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Weave;

namespace WeaveFleet.Infrastructure.Weave;

/// <summary>Reads a Weave package's dist-tags from the npm registry.</summary>
internal sealed partial class NpmWeavePackageVersions(IHttpClientFactory httpClientFactory, ILogger<NpmWeavePackageVersions> logger)
    : IWeavePackageVersions
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(8);

    public async Task<string?> NewestAsync(string package, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LookupTimeout);
        try
        {
            using var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync($"https://registry.npmjs.org/-/package/{package}/dist-tags", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogLookupFailed(logger, package, $"npm answered {(int)response.StatusCode}");
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            return NewestTagged(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            LogLookupFailed(logger, package, ex.Message);
            return null;
        }
    }

    /// <summary>The newer of <c>latest</c> and <c>next</c> in a dist-tags object.</summary>
    internal static string? NewestTagged(JsonElement tags)
    {
        string? Tag(string name) => tags.ValueKind == JsonValueKind.Object
                                    && tags.TryGetProperty(name, out var value)
                                    && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

        return WeavePackageVersion.Newest(Tag("latest"), Tag("next"));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't look up {Package} on npm: {Reason}")]
    private static partial void LogLookupFailed(ILogger logger, string package, string reason);
}
