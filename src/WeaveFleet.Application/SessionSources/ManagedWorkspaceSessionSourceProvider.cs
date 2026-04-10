using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.SessionSources;

public sealed class ManagedWorkspaceSessionSourceProvider(FleetOptions options) : ISessionSourceProvider
{
    public string ProviderId => SessionSourceProviderIds.Managed;

    public IReadOnlyList<SessionSourceDescriptor> GetDescriptors() =>
        options.Cloud.Enabled
            ? [SessionSourceCatalog.ManagedWorkspaceStartSession]
            : [];

    public Task<Result<ResolvedSessionSource>> ResolveAsync(SessionSourceSelection selection, CancellationToken cancellationToken)
    {
        if (!options.Cloud.Enabled)
        {
            return Task.FromResult<Result<ResolvedSessionSource>>(FleetError.ValidationError(
                "SessionSource.Key",
                "Managed workspaces are only available in cloud mode."));
        }

        if (!Matches(selection.Key, SessionSourceCatalog.ManagedWorkspaceStartSession.Key))
        {
            return Task.FromResult<Result<ResolvedSessionSource>>(FleetError.ValidationError(
                "SessionSource.Key",
                $"Source '{selection.Key.ProviderId}/{selection.Key.SourceType}/{selection.Key.ActionId}' is not supported by provider '{ProviderId}'."));
        }

        if (selection.Input.ValueKind is not (System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Undefined))
        {
            return Task.FromResult<Result<ResolvedSessionSource>>(FleetError.ValidationError(
                "SessionSource.Input",
                "Managed workspace session source input must be a JSON object."));
        }

        var placeholderDirectory = options.Cloud.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(placeholderDirectory))
        {
            return Task.FromResult<Result<ResolvedSessionSource>>(FleetError.ValidationError(
                "Cloud.WorkspaceRoot",
                "Cloud.WorkspaceRoot must be configured when Cloud.Enabled is true."));
        }

        return Task.FromResult<Result<ResolvedSessionSource>>(new ResolvedSessionSource(
            SessionSourceCatalog.ManagedWorkspaceStartSession,
            new ResolvedSessionInput(
                new WorkspaceIntent(placeholderDirectory, "managed", null),
                null,
                new ProvenanceRecord(
                    ProviderId,
                    SessionSourceTypeNames.ManagedWorkspace,
                    SessionSourceActions.StartSession,
                    null,
                    null,
                    "Managed workspace",
                    "Workspace managed automatically by cloud mode.",
                    DateTime.UtcNow.ToString("O")))));
    }

    private static bool Matches(SessionSourceKey actual, SessionSourceKey expected) =>
        string.Equals(actual.ProviderId, expected.ProviderId, StringComparison.Ordinal) &&
        string.Equals(actual.SourceType, expected.SourceType, StringComparison.Ordinal) &&
        string.Equals(actual.ActionId, expected.ActionId, StringComparison.Ordinal) &&
        actual.ContractVersion == expected.ContractVersion;
}
