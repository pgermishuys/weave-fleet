using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary><see cref="IBackgroundUserScope"/> over <see cref="BackgroundUserContext"/>, which every <see cref="IUserContext"/> reads first.</summary>
internal sealed class BackgroundUserScope : IBackgroundUserScope
{
    public IDisposable Begin(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return BackgroundUserContext.BeginScope(userId);
    }
}
