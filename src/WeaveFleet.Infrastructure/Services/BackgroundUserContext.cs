using System.Threading;

namespace WeaveFleet.Infrastructure.Services;

internal static class BackgroundUserContext
{
    private static readonly AsyncLocal<string?> CurrentUser = new();

    public static string? UserId => CurrentUser.Value;

    public static IDisposable BeginScope(string userId)
    {
        var previous = CurrentUser.Value;
        CurrentUser.Value = userId;
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(string? previousUserId) : IDisposable
    {
        public void Dispose()
        {
            CurrentUser.Value = previousUserId;
        }
    }
}
