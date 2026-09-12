namespace WeaveFleet.Application.Services;

/// <summary>
/// Runs code as a given user when the request itself doesn't carry that user's identity, for example a
/// harness process calling back into Fleet for one of its sessions. Until the returned scope is disposed,
/// <see cref="IUserContext.UserId"/> is <c>userId</c>.
/// </summary>
public interface IBackgroundUserScope
{
    IDisposable Begin(string userId);
}
