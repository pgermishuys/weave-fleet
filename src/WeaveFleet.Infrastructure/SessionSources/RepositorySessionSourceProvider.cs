using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Infrastructure.SessionSources;

public sealed class RepositorySessionSourceProvider(
    RepositoryService repositoryService) : ISessionSourceProvider
{
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
            input = selection.Input.Deserialize(InfrastructureJsonContext.Default.RepositorySourceInput);
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

        // When an existing worktree path is supplied with the "worktree" strategy,
        // validate that path and use it directly (no new worktree is created).
        if (string.Equals(isolationStrategy.Value, "worktree", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(input.ExistingWorktreePath))
        {
            var existingWorktreePath = input.ExistingWorktreePath.Trim();
            var worktrees = await repositoryService.ListWorktreesAsync(canonicalPath, cancellationToken);
            var knownWorktree = worktrees.FirstOrDefault(w =>
                string.Equals(
                    WorkspaceRootService.CanonicalizePath(w.Path),
                    WorkspaceRootService.CanonicalizePath(existingWorktreePath),
                    StringComparison.OrdinalIgnoreCase));

            if (knownWorktree is null)
            {
                return FleetError.ValidationError(
                    "SessionSource.Input.ExistingWorktreePath",
                    "The specified path is not a known worktree of this repository.");
            }

            // Ensure the worktree path is within allowed workspace roots
            var worktreeRootCheck = await repositoryService.ValidatePathWithinRootsAsync(existingWorktreePath, cancellationToken);
            if (worktreeRootCheck.IsFailure)
            {
                return FleetError.ValidationError(
                    "SessionSource.Input.ExistingWorktreePath",
                    "The worktree path is outside allowed workspace roots.");
            }

            var existingDescriptor = SessionSourceCatalog.RepositoryStartSession with
            {
                DisplayName = repositoryInfo.Name
            };

            return new ResolvedSessionSource(
                existingDescriptor,
                new ResolvedSessionInput(
                    new WorkspaceIntent(
                        WorkspaceRootService.CanonicalizePath(existingWorktreePath),
                        "existing",
                        knownWorktree.Branch),
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
}
