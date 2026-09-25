namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// A harness's "run this shell command" request can last as long as the command does. Fleet waits long enough to hear
/// a refusal (a busy session, a bad request), then lets the command run on: its output arrives as events.
/// </summary>
internal static class ShellCommandCall
{
    /// <summary>How long a refusal can take to come back.</summary>
    internal static readonly TimeSpan RefusalWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Starts <paramref name="call"/> and returns when it ends or after <paramref name="window"/>, whichever is first.
    /// A failure within the window is thrown; one after it goes to <paramref name="onLateFailure"/>, since nobody is
    /// waiting on the call any more.
    /// </summary>
    /// <param name="call">The request, given a token that ends only with <paramref name="lifetime"/>: the caller going away doesn't stop a command that's running.</param>
    public static async Task RunUntilTakenAsync(
        Func<CancellationToken, Task> call,
        Action<Exception> onLateFailure,
        CancellationToken lifetime,
        TimeSpan? window = null)
    {
        var running = Task.Run(() => call(lifetime), CancellationToken.None);
        var first = await Task.WhenAny(running, Task.Delay(window ?? RefusalWindow, CancellationToken.None)).ConfigureAwait(false);
        if (first == running)
        {
            await running.ConfigureAwait(false);
            return;
        }

        _ = running.ContinueWith(
            task => onLateFailure(task.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
