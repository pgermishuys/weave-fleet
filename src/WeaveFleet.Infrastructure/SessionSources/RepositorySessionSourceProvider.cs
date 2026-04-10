using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Infrastructure.SessionSources;

public sealed class RepositorySessionSourceProvider(
    RepositoryService repositoryService) : ISessionSourceProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string ProviderId => SessionSourceProviderIds.Repository;

    public IReadOnlyList<SessionSourceDescriptor> GetDescriptors() =>
    [
        SessionSourceCatalog.RepositoryStartSession
    ];

    public async Task<Result<ResolvedSessionSource>> ResolveAsync(SessionSourceSelection selection, CancellationToken cancellationToken)
    {
        if (!Matches(selection.Key, SessionSourceCatalog.RepositoryStartSession.Key))
        {
            return FleetError.ValidationError(
                "SessionSource.Key",
                $"Source '{selection.Key.ProviderId}/{selection.Key.SourceType}/{selection.Key.ActionId}' is not supported by provider '{ProviderId}'.");
        }

        if (selection.Input.ValueKind != JsonValueKind.Object)
        {
            return FleetError.ValidationError(
                "SessionSource.Input",
                "Session source input must be a JSON object.");
        }

        RepositorySourceInput? input;
        try
        {
            input = selection.Input.Deserialize<RepositorySourceInput>(SerializerOptions);
        }
        catch (JsonException ex)
        {
            return FleetError.ValidationError(
                "SessionSource.Input",
                $"Invalid repository session source payload: {ex.Message}");
        }

        if (input is null || string.IsNullOrWhiteSpace(input.RepositoryPath))
        {
            return FleetError.ValidationError(
                "SessionSource.Input.RepositoryPath",
                "Repository session sources require a repositoryPath.");
        }

        var canonicalPathResult = await repositoryService.ResolveRepositoryPathAsync(input.RepositoryPath, cancellationToken);
        if (canonicalPathResult.IsFailure)
            return canonicalPathResult.Error;

        var canonicalPath = canonicalPathResult.Value;
        var repositoryInfo = await repositoryService.GetRepositoryInfoAsync(canonicalPath, cancellationToken);
        if (repositoryInfo is null)
        {
            return FleetError.ValidationError(
                "SessionSource.Input.RepositoryPath",
                "Repository path must point to a git repository under an allowed workspace root.");
        }

        var isolationStrategy = NormalizeIsolationStrategy(input.IsolationStrategy);
        if (isolationStrategy.IsFailure)
            return isolationStrategy.Error;

        var branch = string.IsNullOrWhiteSpace(input.Branch)
            ? null
            : input.Branch.Trim();

        if (string.Equals(isolationStrategy.Value, "existing", StringComparison.Ordinal) && branch is not null)
        {
            return FleetError.ValidationError(
                "SessionSource.Input.Branch",
                "Branch can only be provided for isolated repository workspaces.");
        }

        var descriptor = SessionSourceCatalog.RepositoryStartSession with
        {
            DisplayName = repositoryInfo.Name
        };

        return new ResolvedSessionSource(
            descriptor,
            new ResolvedSessionInput(
                new WorkspaceIntent(canonicalPath, isolationStrategy.Value, branch),
                null,
                new ProvenanceRecord(
                    ProviderId,
                    SessionSourceTypeNames.Repository,
                    SessionSourceActions.StartSession,
                    canonicalPath,
                    null,
                    repositoryInfo.Name,
                    null,
                    DateTime.UtcNow.ToString("O"))));
    }

    private static Result<string> NormalizeIsolationStrategy(string? isolationStrategy)
    {
        var value = string.IsNullOrWhiteSpace(isolationStrategy)
            ? "existing"
            : isolationStrategy.Trim();

        if (value is not ("existing" or "worktree"))
        {
            return FleetError.ValidationError(
                "SessionSource.Input.IsolationStrategy",
                "Repository session sources currently support only 'existing' and 'worktree'.");
        }

        return value;
    }

    private static bool Matches(SessionSourceKey actual, SessionSourceKey expected) =>
        string.Equals(actual.ProviderId, expected.ProviderId, StringComparison.Ordinal) &&
        string.Equals(actual.SourceType, expected.SourceType, StringComparison.Ordinal) &&
        string.Equals(actual.ActionId, expected.ActionId, StringComparison.Ordinal) &&
        actual.ContractVersion == expected.ContractVersion;

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record RepositorySourceInput
    {
        public string? RepositoryPath { get; init; }
        public string? IsolationStrategy { get; init; }
        public string? Branch { get; init; }
    }
}
