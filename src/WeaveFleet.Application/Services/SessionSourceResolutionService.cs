using System.Text.Json;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Services;

public sealed class SessionSourceResolutionService(IEnumerable<ISessionSourceProvider> providers, FleetOptions options)
{
    private readonly Dictionary<string, ISessionSourceProvider> _providers = providers
        .ToDictionary(provider => provider.ProviderId, StringComparer.Ordinal);

    public SessionSourceResolutionService(IEnumerable<ISessionSourceProvider> providers)
        : this(providers, new FleetOptions())
    {
    }

    public Task<Result<ResolvedSessionSource>> ResolveCreateRequestAsync(CreateSessionRequest request, CancellationToken cancellationToken)
    {
        var selectionResult = request.Source is not null
            ? ValidateClientSelection(request.Source)
            : TranslateLegacyRequest(request);

        if (selectionResult.IsFailure)
            return Task.FromResult<Result<ResolvedSessionSource>>(selectionResult.Error);

        return ResolveAsync(selectionResult.Value, cancellationToken);
    }

    public Task<Result<ResolvedSessionSource>> ResolveAsync(SessionSourceSelection selection, CancellationToken cancellationToken)
    {
        var keyValidation = ValidateSourceKey(selection.Key);
        if (keyValidation.IsFailure)
            return Task.FromResult<Result<ResolvedSessionSource>>(keyValidation.Error);

        if (!_providers.TryGetValue(selection.Key.ProviderId, out var provider))
            return Task.FromResult<Result<ResolvedSessionSource>>(FleetError.ValidationError(
                "SessionSource.ProviderId",
                $"Unknown session source provider '{selection.Key.ProviderId}'."));

        var descriptor = provider.GetDescriptors().FirstOrDefault(candidate =>
            string.Equals(candidate.Key.ProviderId, selection.Key.ProviderId, StringComparison.Ordinal) &&
            string.Equals(candidate.Key.SourceType, selection.Key.SourceType, StringComparison.Ordinal) &&
            string.Equals(candidate.Key.ActionId, selection.Key.ActionId, StringComparison.Ordinal) &&
            candidate.Key.ContractVersion == selection.Key.ContractVersion);

        if (descriptor is null)
            return Task.FromResult<Result<ResolvedSessionSource>>(FleetError.ValidationError(
                "SessionSource.Action",
                $"Session source action '{selection.Key.ActionId}' is not supported for '{selection.Key.ProviderId}/{selection.Key.SourceType}'."));

        return provider.ResolveAsync(selection, cancellationToken);
    }

    public Task<Result<ResolvedSessionSource>> ResolveForSessionActionAsync(string sessionId, SessionSourceSelection selection, string actionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult<Result<ResolvedSessionSource>>(FleetError.ValidationError(
                "Session.Id",
                "Session id is required."));
        }

        if (!string.Equals(selection.Key.ActionId, actionId, StringComparison.Ordinal))
        {
            return Task.FromResult<Result<ResolvedSessionSource>>(FleetError.ValidationError(
                "SessionSource.ActionId",
                $"Session source action must be '{actionId}'."));
        }

        return ResolveAsync(selection, cancellationToken);
    }

    private static Result<SessionSourceSelection> ValidateClientSelection(SessionSourceSelection selection)
    {
        var keyValidation = ValidateSourceKey(selection.Key);
        if (keyValidation.IsFailure)
            return keyValidation.Error;

        return selection;
    }

    private Result<SessionSourceSelection> TranslateLegacyRequest(CreateSessionRequest request)
    {
        if (request.Source is null && options.Cloud.Enabled && string.IsNullOrWhiteSpace(request.Directory))
        {
            return new SessionSourceSelection
            {
                Key = SessionSourceCatalog.ManagedWorkspaceStartSession.Key,
                Input = JsonSerializer.SerializeToElement(new { })
            };
        }

        if (string.IsNullOrWhiteSpace(request.Directory))
            return FleetError.ValidationError(
                "SessionSource.Selection",
                "Provide either a legacy directory payload or a source selection payload.");

        return new SessionSourceSelection
        {
            Key = new SessionSourceKey
            {
                ProviderId = SessionSourceProviderIds.Local,
                SourceType = SessionSourceTypeNames.Directory,
                ActionId = SessionSourceActions.StartSession,
                ContractVersion = 1
            },
            Input = BuildLegacyDirectoryInput(request.Directory, request.IsolationStrategy, request.Branch)
        };
    }

    private static JsonElement BuildLegacyDirectoryInput(string directory, string? isolationStrategy, string? branch)
    {
        return JsonSerializer.SerializeToElement(new LegacyDirectoryInput
        {
            Directory = directory,
            IsolationStrategy = isolationStrategy,
            Branch = branch
        });
    }

    private static Result<Unit> ValidateSourceKey(SessionSourceKey key)
    {
        if (string.IsNullOrWhiteSpace(key.ProviderId))
            return FleetError.ValidationError("SessionSource.ProviderId", "Session source provider id is required.");

        if (string.IsNullOrWhiteSpace(key.SourceType))
            return FleetError.ValidationError("SessionSource.SourceType", "Session source type is required.");

        if (string.IsNullOrWhiteSpace(key.ActionId))
            return FleetError.ValidationError("SessionSource.ActionId", "Session source action id is required.");

        if (key.ContractVersion <= 0)
            return FleetError.ValidationError("SessionSource.ContractVersion", "Session source contract version must be greater than zero.");

        return Unit.Value;
    }

    private sealed record LegacyDirectoryInput
    {
        public required string Directory { get; init; }
        public string? IsolationStrategy { get; init; }
        public string? Branch { get; init; }
    }
}
