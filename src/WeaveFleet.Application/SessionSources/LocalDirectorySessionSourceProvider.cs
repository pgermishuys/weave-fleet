using WeaveFleet.Application.Services;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.SessionSources;

public sealed class LocalDirectorySessionSourceProvider(
    WorkspaceRootService workspaceRootService) : ISessionSourceProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string ProviderId => SessionSourceProviderIds.Local;

    public IReadOnlyList<SessionSourceDescriptor> GetDescriptors() =>
    [
        SessionSourceCatalog.DirectoryStartSession
    ];

    public async Task<Result<ResolvedSessionSource>> ResolveAsync(SessionSourceSelection selection, CancellationToken cancellationToken)
    {
        if (!Matches(selection.Key, SessionSourceCatalog.DirectoryStartSession.Key))
            return FleetError.ValidationError(
                "SessionSource.Key",
                $"Source '{selection.Key.ProviderId}/{selection.Key.SourceType}/{selection.Key.ActionId}' is not supported by provider '{ProviderId}'.");

        if (selection.Input.ValueKind != JsonValueKind.Object)
            return FleetError.ValidationError(
                "SessionSource.Input",
                "Session source input must be a JSON object.");

        DirectorySourceInput? input;

        try
        {
            input = selection.Input.Deserialize<DirectorySourceInput>(SerializerOptions);
        }
        catch (JsonException ex)
        {
            return FleetError.ValidationError(
                "SessionSource.Input",
                $"Invalid directory session source payload: {ex.Message}");
        }

        if (input is null || string.IsNullOrWhiteSpace(input.Directory))
            return FleetError.ValidationError(
                "SessionSource.Input.Directory",
                "Directory session sources require a non-empty directory path.");

        var canonicalDirectoryResult = await workspaceRootService.ResolvePathWithinAllowedRootsAsync(input.Directory);
        if (canonicalDirectoryResult.IsFailure)
            return canonicalDirectoryResult.Error;

        var canonicalDirectory = canonicalDirectoryResult.Value;

        var isolationStrategy = string.IsNullOrWhiteSpace(input.IsolationStrategy)
            ? "existing"
            : input.IsolationStrategy.Trim();

        var resolved = new ResolvedSessionSource(
            SessionSourceCatalog.DirectoryStartSession,
            new ResolvedSessionInput(
                new WorkspaceIntent(canonicalDirectory, isolationStrategy, input.Branch),
                null,
                new ProvenanceRecord(
                    ProviderId,
                    SessionSourceTypeNames.Directory,
                    SessionSourceActions.StartSession,
                    canonicalDirectory,
                    null,
                    Path.GetFileName(canonicalDirectory),
                    null,
                    DateTime.UtcNow.ToString("O"))));

        return resolved;
    }

    private static bool Matches(SessionSourceKey actual, SessionSourceKey expected) =>
        string.Equals(actual.ProviderId, expected.ProviderId, StringComparison.Ordinal) &&
        string.Equals(actual.SourceType, expected.SourceType, StringComparison.Ordinal) &&
        string.Equals(actual.ActionId, expected.ActionId, StringComparison.Ordinal) &&
        actual.ContractVersion == expected.ContractVersion;

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record DirectorySourceInput
    {
        public string? Directory { get; init; }
        public string? IsolationStrategy { get; init; }
        public string? Branch { get; init; }
    }
}
