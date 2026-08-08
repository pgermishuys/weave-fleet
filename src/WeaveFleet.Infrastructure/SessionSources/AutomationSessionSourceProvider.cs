using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.SessionSources;

public sealed class AutomationSessionSourceProvider(
    IAutomationRepository automationRepository,
    WorkspaceRootService workspaceRootService) : ISessionSourceProvider
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

        // Determine workspace directory and isolation strategy
        string workspaceDirectory;
        string isolationStrategy;

        if (!string.IsNullOrWhiteSpace(automation.WorkspaceId))
        {
            // Automation has a specific workspace configured
            var canonicalDirectoryResult = await workspaceRootService.ResolvePathWithinAllowedRootsAsync(automation.WorkspaceId);
            if (canonicalDirectoryResult.IsFailure)
            {
                return FleetError.ValidationError(
                    "SessionSource.Automation.WorkspaceId",
                    $"Automation workspace '{automation.WorkspaceId}' is not within allowed roots: {canonicalDirectoryResult.Error.Description}");
            }

            workspaceDirectory = canonicalDirectoryResult.Value;
            isolationStrategy = "existing";
        }
        else
        {
            // No workspace configured — fall back to the first allowed workspace root.
            // A null WorkspaceIntent is rejected by the orchestrator, so we must resolve a directory.
            var roots = await workspaceRootService.GetAllowedRootsAsync();
            if (roots.Count == 0)
            {
                return FleetError.ValidationError(
                    "SessionSource.Automation.WorkspaceId",
                    "No workspace configured on this automation and no workspace roots are registered. Configure a workspace on the automation or add a workspace root in settings.");
            }

            workspaceDirectory = roots[0];
            isolationStrategy = "existing";
        }

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
                new WorkspaceIntent(workspaceDirectory, isolationStrategy, null),
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

    private static bool Matches(SessionSourceKey actual, SessionSourceKey expected) =>
        string.Equals(actual.ProviderId, expected.ProviderId, StringComparison.Ordinal) &&
        string.Equals(actual.SourceType, expected.SourceType, StringComparison.Ordinal) &&
        string.Equals(actual.ActionId, expected.ActionId, StringComparison.Ordinal) &&
        actual.ContractVersion == expected.ContractVersion;
}
