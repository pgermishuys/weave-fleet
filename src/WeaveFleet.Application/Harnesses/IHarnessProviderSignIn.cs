using System.Text.Json;

namespace WeaveFleet.Application.Harnesses;

/// <summary>
/// A harness's own provider sign-ins (API keys, OAuth logins), for harnesses that keep them themselves and can change
/// them while Fleet runs (<see cref="Domain.Harnesses.HarnessCapabilities.SupportsProviderSignIn"/>). Keys and codes
/// pass through to the harness and nowhere else: implementations never log, store or echo them.
/// </summary>
/// <remarks>
/// Methods throw <see cref="HarnessSignInException"/> when the harness refuses a request, with its reason.
/// </remarks>
public interface IHarnessProviderSignIn
{
    /// <summary>Every provider the harness can sign in to, how, and the sign-ins it has.</summary>
    Task<HarnessSignIns> ListAsync(string ownerUserId, CancellationToken ct);

    /// <summary>Signs in to <paramref name="providerId"/> with an API key and the key method's other fields.</summary>
    Task SignInWithKeyAsync(
        string ownerUserId,
        string providerId,
        string key,
        IReadOnlyDictionary<string, JsonElement> answers,
        CancellationToken ct);

    /// <summary>Starts a browser sign-in (OAuth) with method <paramref name="methodId"/>; follow it with <see cref="GetAttemptAsync"/>.</summary>
    Task<HarnessSignInAttempt> StartAsync(
        string ownerUserId,
        string providerId,
        string methodId,
        IReadOnlyDictionary<string, JsonElement> answers,
        CancellationToken ct);

    /// <summary>Where a browser sign-in is.</summary>
    Task<HarnessSignInAttemptStatus> GetAttemptAsync(string ownerUserId, string providerId, string attemptId, CancellationToken ct);

    /// <summary>Finishes a sign-in that asked for a code (<see cref="HarnessSignInAttempt.NeedsCode"/>).</summary>
    Task SubmitCodeAsync(string ownerUserId, string providerId, string attemptId, string code, CancellationToken ct);

    /// <summary>
    /// Finishes a sign-in whose provider sent the browser back to <see cref="HarnessSignInAttempt.CallbackAddress"/>,
    /// from a browser that couldn't reach it (another device): <paramref name="landedOn"/> is the address that browser
    /// landed on, and only its query goes on to the callback address.
    /// </summary>
    Task ForwardCallbackAsync(string ownerUserId, string providerId, string attemptId, Uri landedOn, CancellationToken ct);

    /// <summary>Stops a browser sign-in and releases what it holds (a callback listener); a finished one is left alone.</summary>
    Task CancelAsync(string ownerUserId, string providerId, string attemptId, CancellationToken ct);

    /// <summary>Makes sign-in <paramref name="connectionId"/> the one its provider uses.</summary>
    Task UseAsync(string ownerUserId, string connectionId, CancellationToken ct);

    /// <summary>Removes sign-in <paramref name="connectionId"/>; the provider's next sign-in, if any, takes over.</summary>
    Task SignOutAsync(string ownerUserId, string connectionId, CancellationToken ct);
}

/// <summary>The providers a harness can sign in to, and a line about where its sign-ins are kept.</summary>
public sealed record HarnessSignIns(IReadOnlyList<HarnessSignInProvider> Providers, string? Note);

/// <summary>A provider, how to sign in to it, and its sign-ins (the one in use first).</summary>
public sealed record HarnessSignInProvider(
    string Id,
    string Name,
    IReadOnlyList<HarnessSignInMethod> Methods,
    IReadOnlyList<HarnessSignInConnection> Connections);

/// <summary>The kinds of <see cref="HarnessSignInMethod"/>.</summary>
public static class HarnessSignInMethodTypes
{
    /// <summary>An API key, with the <see cref="HarnessSignInMethod.Fields"/> the provider needs next to it.</summary>
    public const string Key = "key";

    /// <summary>A sign-in in the browser; <see cref="HarnessSignInMethod.Id"/> names it.</summary>
    public const string OAuth = "oauth";

    /// <summary>A command the harness runs to get a key (<see cref="HarnessSignInMethod.Command"/>). Fleet doesn't run it.</summary>
    public const string Command = "command";

    /// <summary>Environment variables the harness reads (<see cref="HarnessSignInMethod.EnvironmentVariables"/>).</summary>
    public const string Environment = "env";
}

/// <summary>One way to sign in to a provider; <see cref="Type"/> is one of <see cref="HarnessSignInMethodTypes"/>.</summary>
public sealed record HarnessSignInMethod(string Type, string? Id, string Label)
{
    /// <summary>What the method asks for besides the key or browser: an account id, a deployment type.</summary>
    public IReadOnlyList<HarnessSignInField> Fields { get; init; } = [];

    /// <summary>For <see cref="HarnessSignInMethodTypes.Command"/>: the command and its arguments.</summary>
    public IReadOnlyList<string>? Command { get; init; }

    /// <summary>For <see cref="HarnessSignInMethodTypes.Environment"/>: the variables the harness reads.</summary>
    public IReadOnlyList<string>? EnvironmentVariables { get; init; }
}

/// <summary>
/// A field a sign-in method asks for. <see cref="Type"/> is <c>string</c> (a choice when it has
/// <see cref="Options"/>), <c>boolean</c>, <c>number</c>, <c>integer</c>, <c>multiselect</c> or <c>external</c> (a link
/// to <see cref="Url"/>). It's asked only when every condition in <see cref="When"/> holds.
/// </summary>
public sealed record HarnessSignInField(string Key, string Type)
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }

    /// <summary>Not asked; <see cref="Default"/> is sent.</summary>
    public bool Hidden { get; init; }

    public string? Placeholder { get; init; }
    public JsonElement? Default { get; init; }
    public IReadOnlyList<HarnessSignInOption>? Options { get; init; }
    public IReadOnlyList<HarnessSignInCondition>? When { get; init; }
    public string? Url { get; init; }
}

/// <summary>A choice for a <see cref="HarnessSignInField"/>; <see cref="Value"/> is what's sent.</summary>
public sealed record HarnessSignInOption(string Value, string Label, string? Description = null);

/// <summary>A field is asked when field <see cref="Key"/>'s answer is (<c>eq</c>) or isn't (<c>neq</c>) <see cref="Value"/>.</summary>
public sealed record HarnessSignInCondition(string Key, string Op, JsonElement Value);

/// <summary>The kinds of <see cref="HarnessSignInConnection"/>.</summary>
public static class HarnessSignInConnectionKinds
{
    /// <summary>A sign-in the harness keeps: Fleet can switch to it or remove it.</summary>
    public const string Credential = "credential";

    /// <summary>A key in the harness's environment (<see cref="HarnessSignInConnection.Id"/> is the variable's name).</summary>
    public const string Environment = "env";
}

/// <summary>A sign-in a provider has. <see cref="Active"/>: it's the one the provider uses.</summary>
public sealed record HarnessSignInConnection(string Kind, string Id, string Label, bool Active);

/// <summary>A browser sign-in the harness started.</summary>
/// <param name="Url">The provider's sign-in page.</param>
/// <param name="Instructions">What to do there, in the harness's words; may carry a code to enter.</param>
/// <param name="NeedsCode">The provider shows a code to paste back (<see cref="IHarnessProviderSignIn.SubmitCodeAsync"/>).</param>
public sealed record HarnessSignInAttempt(string Id, string Url, string Instructions, bool NeedsCode, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// When the provider sends the browser back to a listener on the machine Fleet runs on (a <c>localhost</c>
    /// address), that address. A browser on another device can't reach it; the address it lands on can be passed to
    /// <see cref="IHarnessProviderSignIn.ForwardCallbackAsync"/> instead.
    /// </summary>
    public string? CallbackAddress { get; init; }
}

/// <summary>The states of a <see cref="HarnessSignInAttemptStatus"/>.</summary>
public static class HarnessSignInAttemptStates
{
    public const string Pending = "pending";
    public const string Complete = "complete";
    public const string Failed = "failed";
    public const string Expired = "expired";

    /// <summary>The harness no longer knows the attempt: cancelled, or finished a while ago.</summary>
    public const string Gone = "gone";
}

/// <summary>Where a browser sign-in is: one of <see cref="HarnessSignInAttemptStates"/>, and why it failed.</summary>
public sealed record HarnessSignInAttemptStatus(string Status, string? Message = null);

/// <summary>The harness refused a sign-in request; <see cref="Exception.Message"/> is its reason, safe to show.</summary>
public sealed class HarnessSignInException(string message, bool notFound = false) : Exception(message)
{
    /// <summary>The provider, method, attempt or sign-in doesn't exist.</summary>
    public bool NotFound { get; } = notFound;
}
