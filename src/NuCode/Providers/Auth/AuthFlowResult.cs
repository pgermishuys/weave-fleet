namespace NuCode.Providers.Auth;

/// <summary>
/// The result of executing an auth flow for a provider.
/// </summary>
public abstract record AuthFlowResult
{
    private AuthFlowResult() { }

    /// <summary>Authentication completed successfully. Credentials have been stored.</summary>
    public sealed record Success : AuthFlowResult;

    /// <summary>
    /// Authentication requires user action before it can complete.
    /// The caller should display <see cref="Instructions"/> to the user and poll via
    /// <see cref="PollAsync"/> until the flow completes or times out.
    /// </summary>
    public sealed record NeedsUserAction(
        string Instructions,
        Func<CancellationToken, Task<AuthFlowResult>> PollAsync) : AuthFlowResult;

    /// <summary>Authentication failed with an error message.</summary>
    public sealed record Failed(string Error) : AuthFlowResult;
}
