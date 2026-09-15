using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.SessionSources;

public sealed partial class AutomationSessionSourceProvider(
    IAutomationRepository automationRepository,
    WorkspaceRootService workspaceRootService,
    RepositoryService repositoryService,
    TimeProvider timeProvider) : ISessionSourceProvider
{
    public string ProviderId => SessionSourceProviderIds.Automation;

    public IReadOnlyList<SessionSourceDescriptor> GetDescriptors() =>
    [
        SessionSourceCatalog.AutomationStartSession
    ];

    public async Task<Result<ResolvedSessionSource>> ResolveAsync(SessionSourceSelection selection, CancellationToken cancellationToken)
    {
        if (!Matches(selection.Key, SessionSourceCatalog.AutomationStartSession.Key))
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

        AutomationSourceInput? input;
        try
        {
            input = selection.Input.Deserialize(InfrastructureJsonContext.Default.AutomationSourceInput);
        }
        catch (JsonException ex)
        {
            return FleetError.ValidationError(
                "SessionSource.Input",
                $"Invalid automation session source payload: {ex.Message}");
        }

        if (input is null || string.IsNullOrWhiteSpace(input.AutomationId))
        {
            return FleetError.ValidationError(
                "SessionSource.Input.AutomationId",
                "Automation session sources require an automationId.");
        }

        // Fetch the automation to get its workspace configuration
        var automation = await automationRepository.GetByIdAsync(input.AutomationId);
        if (automation is null)
        {
            return FleetError.ValidationError(
                "SessionSource.Input.AutomationId",
                $"Automation '{input.AutomationId}' not found.");
        }

        var workspaceIntent = await ResolveWorkspaceIntentAsync(automation, cancellationToken);
        if (workspaceIntent.IsFailure)
            return workspaceIntent.Error;

        var trigger = string.IsNullOrWhiteSpace(input.Trigger)
            ? "manual"
            : input.Trigger.Trim();

        var displayName = string.IsNullOrWhiteSpace(input.AutomationName)
            ? automation.Name
            : input.AutomationName;

        var descriptor = SessionSourceCatalog.AutomationStartSession with
        {
            DisplayName = displayName
        };

        var resolved = new ResolvedSessionSource(
            descriptor,
            new ResolvedSessionInput(
                workspaceIntent.Value,
                null,
                new ProvenanceRecord(
                    ProviderId,
                    SessionSourceTypeNames.Automation,
                    SessionSourceActions.StartSession,
                    automation.Id,
                    null,
                    displayName,
                    $"Triggered by {trigger}",
                    DateTime.UtcNow.ToString("O"))));

        return resolved;
    }

    /// <summary>
    /// Where a run happens. A worktree run gets a new worktree of the folder, on a branch named after the automation
    /// and the time. An "existing" run uses the folder as it is, or a scratch folder when there is none. An automation
    /// made before this was stored runs as it always has: in its folder, or in the first workspace root.
    /// </summary>
    private async Task<Result<WorkspaceIntent>> ResolveWorkspaceIntentAsync(Automation automation, CancellationToken ct)
    {
        var folder = string.IsNullOrWhiteSpace(automation.WorkspaceId) ? null : automation.WorkspaceId;

        if (automation.Isolation == "worktree" && folder is not null)
        {
            var repository = await repositoryService.ResolveRepositoryPathAsync(folder, ct);
            if (repository.IsFailure)
            {
                return FleetError.ValidationError(
                    "SessionSource.Automation.WorkspaceId",
                    $"Can't make a worktree of {folder}: {repository.Error.Description}");
            }

            return new WorkspaceIntent(repository.Value, "worktree", RunBranchName(automation), automation.BaseBranch);
        }

        if (folder is not null)
        {
            var canonicalDirectoryResult = await workspaceRootService.ResolvePathWithinAllowedRootsAsync(folder);
            if (canonicalDirectoryResult.IsFailure)
            {
                return FleetError.ValidationError(
                    "SessionSource.Automation.WorkspaceId",
                    $"{folder} isn't inside a workspace root. Add it in Settings → Workspace.");
            }

            return new WorkspaceIntent(canonicalDirectoryResult.Value, "existing", null);
        }

        if (automation.Isolation is not null)
        {
            // No folder: a scratch folder of its own, like a quick chat.
            var scratch = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".weave-fleet",
                "automation-runs",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            return new WorkspaceIntent(scratch, "existing", null);
        }

        // Made before isolation was stored, with no folder: the first workspace root, as before.
        // A null WorkspaceIntent is rejected by the orchestrator, so we must resolve a directory.
        var roots = await workspaceRootService.GetAllowedRootsAsync();
        if (roots.Count == 0)
        {
            return FleetError.ValidationError(
                "SessionSource.Automation.WorkspaceId",
                "No workspace configured on this automation and no workspace roots are registered. Configure a workspace on the automation or add a workspace root in settings.");
        }

        return new WorkspaceIntent(roots[0], "existing", null);
    }

    /// <summary><c>fleet/auto-weekly-pr-digest-20260921-0900</c>: the automation's name and the run's time on its clock.</summary>
    internal string RunBranchName(Automation automation)
    {
        var slug = NonSlugCharacters().Replace(automation.Name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('-');

        var local = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), AutomationSchedule.ResolveTimeZone(automation.TimeZone));
        var stamp = local.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(slug) ? $"fleet/auto-{stamp}" : $"fleet/auto-{slug}-{stamp}";
    }

    [System.Text.RegularExpressions.GeneratedRegex("[^a-z0-9]+")]
    private static partial System.Text.RegularExpressions.Regex NonSlugCharacters();

    private static bool Matches(SessionSourceKey actual, SessionSourceKey expected) =>
        string.Equals(actual.ProviderId, expected.ProviderId, StringComparison.Ordinal) &&
        string.Equals(actual.SourceType, expected.SourceType, StringComparison.Ordinal) &&
        string.Equals(actual.ActionId, expected.ActionId, StringComparison.Ordinal) &&
        actual.ContractVersion == expected.ContractVersion;
}
