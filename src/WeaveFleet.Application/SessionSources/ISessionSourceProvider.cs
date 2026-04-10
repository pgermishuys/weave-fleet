using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.SessionSources;

public interface ISessionSourceProvider
{
    string ProviderId { get; }

    IReadOnlyList<SessionSourceDescriptor> GetDescriptors();

    Task<Result<ResolvedSessionSource>> ResolveAsync(SessionSourceSelection selection, CancellationToken cancellationToken);
}
