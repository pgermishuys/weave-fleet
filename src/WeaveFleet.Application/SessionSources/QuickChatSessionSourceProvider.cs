using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.SessionSources;

public sealed class QuickChatSessionSourceProvider : ISessionSourceProvider
{
    public string ProviderId => SessionSourceProviderIds.QuickChat;

    public IReadOnlyList<SessionSourceDescriptor> GetDescriptors() => [SessionSourceCatalog.QuickChatStartSession];

    public Task<Result<ResolvedSessionSource>> ResolveAsync(SessionSourceSelection selection, CancellationToken cancellationToken)
    {
        if (!Matches(selection.Key, SessionSourceCatalog.QuickChatStartSession.Key))
        {
            return Task.FromResult<Result<ResolvedSessionSource>>(
                FleetError.ValidationError("SessionSource.Key", $"Source '{selection.Key.ProviderId}/{selection.Key.SourceType}/{selection.Key.ActionId}' is not supported by provider '{ProviderId}'."));
        }

        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".weave-fleet",
            "quick-chats");

        var uniqueDirName = Guid.NewGuid().ToString("N");
        var fullPath = Path.Combine(basePath, uniqueDirName);

        Directory.CreateDirectory(fullPath);

        var resolved = new ResolvedSessionSource(
            SessionSourceCatalog.QuickChatStartSession,
            new ResolvedSessionInput(
                new WorkspaceIntent(fullPath, "existing", null),
                null,
                new ProvenanceRecord(
                    ProviderId,
                    SessionSourceTypeNames.QuickChat,
                    SessionSourceActions.StartSession,
                    null,
                    null,
                    "Quick Chat",
                    null,
                    DateTime.UtcNow.ToString("O"))));

        return Task.FromResult<Result<ResolvedSessionSource>>(resolved);
    }

    private static bool Matches(SessionSourceKey actual, SessionSourceKey expected) =>
        string.Equals(actual.ProviderId, expected.ProviderId, StringComparison.Ordinal) &&
        string.Equals(actual.SourceType, expected.SourceType, StringComparison.Ordinal) &&
        string.Equals(actual.ActionId, expected.ActionId, StringComparison.Ordinal) &&
        actual.ContractVersion == expected.ContractVersion;
}
