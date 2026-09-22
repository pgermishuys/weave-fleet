using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Signs the user in to a harness's providers from Settings, through the harness's own sign-in
/// (<see cref="IHarnessProviderSignIn"/>). Keys and codes go from the request to the harness and nowhere else: this
/// service never logs, stores or repeats them, and its errors never contain them.
/// </summary>
public sealed partial class HarnessSignInService(
    IHarnessRegistry harnessRegistry,
    IUserContext userContext,
    FleetOptions options,
    ILogger<HarnessSignInService> logger)
{
    public const int MaxIdLength = 256;
    public const int MaxKeyLength = 16 * 1024;
    public const int MaxCodeLength = 4 * 1024;
    public const int MaxAddressLength = 8 * 1024;
    public const int MaxAnswers = 32;

    /// <summary>
    /// Whether Fleet offers sign-in for this harness. Not with auth on: a harness's sign-ins are the machine's, shared
    /// by every Fleet user's sessions, so one user's key would pay for everyone's. Not in cloud mode either, where the
    /// harness runs on the server rather than the user's computer.
    /// </summary>
    public static bool Supports(Domain.Harnesses.HarnessCapabilities capabilities, FleetOptions options) =>
        capabilities.SupportsProviderSignIn && !options.Auth.Enabled && !options.Cloud.Enabled;

    public Task<Result<HarnessSignIns>> ListAsync(string harnessType, CancellationToken ct)
        => RunAsync(harnessType, "list its providers", signIn => signIn.ListAsync(userContext.UserId, ct));

    public Task<Result<Unit>> SignInWithKeyAsync(
        string harnessType,
        string providerId,
        string? key,
        IReadOnlyDictionary<string, JsonElement>? answers,
        CancellationToken ct)
    {
        if ((ValidateId(providerId, "provider") ?? ValidateAnswers(answers)) is { } invalid)
            return Task.FromResult<Result<Unit>>(invalid);
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult<Result<Unit>>(FleetError.ValidationError("SignIn.Key", "Enter the key."));
        if (key.Length > MaxKeyLength)
            return Task.FromResult<Result<Unit>>(FleetError.ValidationError("SignIn.Key", "That key is too long."));

        return RunAsync(harnessType, "sign in", async signIn =>
        {
            await signIn.SignInWithKeyAsync(userContext.UserId, providerId, key.Trim(), answers ?? Empty, ct).ConfigureAwait(false);
            return Unit.Value;
        });
    }

    public Task<Result<HarnessSignInAttempt>> StartAsync(
        string harnessType,
        string providerId,
        string? methodId,
        IReadOnlyDictionary<string, JsonElement>? answers,
        CancellationToken ct)
    {
        if ((ValidateId(providerId, "provider") ?? ValidateId(methodId, "sign-in method") ?? ValidateAnswers(answers)) is { } invalid)
            return Task.FromResult<Result<HarnessSignInAttempt>>(invalid);

        return RunAsync(harnessType, "start the sign-in",
            signIn => signIn.StartAsync(userContext.UserId, providerId, methodId!, answers ?? Empty, ct));
    }

    public Task<Result<HarnessSignInAttemptStatus>> GetAttemptAsync(string harnessType, string providerId, string attemptId, CancellationToken ct)
    {
        if ((ValidateId(providerId, "provider") ?? ValidateId(attemptId, "sign-in")) is { } invalid)
            return Task.FromResult<Result<HarnessSignInAttemptStatus>>(invalid);

        return RunAsync(harnessType, "check the sign-in",
            signIn => signIn.GetAttemptAsync(userContext.UserId, providerId, attemptId, ct));
    }

    public Task<Result<Unit>> SubmitCodeAsync(string harnessType, string providerId, string attemptId, string? code, CancellationToken ct)
    {
        if ((ValidateId(providerId, "provider") ?? ValidateId(attemptId, "sign-in")) is { } invalid)
            return Task.FromResult<Result<Unit>>(invalid);
        if (string.IsNullOrWhiteSpace(code))
            return Task.FromResult<Result<Unit>>(FleetError.ValidationError("SignIn.Code", "Paste the code the provider showed you."));
        if (code.Length > MaxCodeLength)
            return Task.FromResult<Result<Unit>>(FleetError.ValidationError("SignIn.Code", "That code is too long."));

        return RunAsync(harnessType, "finish the sign-in", async signIn =>
        {
            await signIn.SubmitCodeAsync(userContext.UserId, providerId, attemptId, code.Trim(), ct).ConfigureAwait(false);
            return Unit.Value;
        });
    }

    /// <summary>
    /// Passes on the address a browser on another device landed on after the provider's sign-in page (a
    /// <c>localhost</c> page it couldn't open), to the listener waiting for it on this machine.
    /// </summary>
    public Task<Result<Unit>> ForwardCallbackAsync(string harnessType, string providerId, string attemptId, string? landedOn, CancellationToken ct)
    {
        if ((ValidateId(providerId, "provider") ?? ValidateId(attemptId, "sign-in")) is { } invalid)
            return Task.FromResult<Result<Unit>>(invalid);

        var text = landedOn?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxAddressLength
            || !Uri.TryCreate(text, UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
            || address.Query.Length <= 1)
        {
            return Task.FromResult<Result<Unit>>(FleetError.ValidationError("SignIn.Address",
                "Paste the whole address from the page that didn't load, starting with http://localhost."));
        }

        return RunAsync(harnessType, "finish the sign-in", async signIn =>
        {
            await signIn.ForwardCallbackAsync(userContext.UserId, providerId, attemptId, address, ct).ConfigureAwait(false);
            return Unit.Value;
        });
    }

    public Task<Result<Unit>> CancelAsync(string harnessType, string providerId, string attemptId, CancellationToken ct)
    {
        if ((ValidateId(providerId, "provider") ?? ValidateId(attemptId, "sign-in")) is { } invalid)
            return Task.FromResult<Result<Unit>>(invalid);

        return RunAsync(harnessType, "cancel the sign-in", async signIn =>
        {
            await signIn.CancelAsync(userContext.UserId, providerId, attemptId, ct).ConfigureAwait(false);
            return Unit.Value;
        });
    }

    public Task<Result<Unit>> UseAsync(string harnessType, string connectionId, CancellationToken ct)
    {
        if (ValidateId(connectionId, "sign-in") is { } invalid)
            return Task.FromResult<Result<Unit>>(invalid);

        return RunAsync(harnessType, "switch sign-ins", async signIn =>
        {
            await signIn.UseAsync(userContext.UserId, connectionId, ct).ConfigureAwait(false);
            return Unit.Value;
        });
    }

    public Task<Result<Unit>> SignOutAsync(string harnessType, string connectionId, CancellationToken ct)
    {
        if (ValidateId(connectionId, "sign-in") is { } invalid)
            return Task.FromResult<Result<Unit>>(invalid);

        return RunAsync(harnessType, "sign out", async signIn =>
        {
            await signIn.SignOutAsync(userContext.UserId, connectionId, ct).ConfigureAwait(false);
            return Unit.Value;
        });
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> Empty = new Dictionary<string, JsonElement>();

    /// <summary>
    /// Runs <paramref name="action"/> on the harness's sign-in. The harness's refusals come back as its reason; anything
    /// else as a sentence of Fleet's own, since only the harness's messages are known not to repeat what was sent.
    /// </summary>
    private async Task<Result<T>> RunAsync<T>(string harnessType, string doing, Func<IHarnessProviderSignIn, Task<T>> action)
    {
        var harness = harnessRegistry.GetByType(harnessType);
        if (harness is null)
            return FleetError.NotFoundFor("Harness", harnessType);
        if (!Supports(harness.Capabilities, options) || harnessRegistry.GetRuntimeByType(harnessType)?.ProviderSignIn is not { } signIn)
        {
            return FleetError.ValidationError("SignIn.Unsupported", options.Auth.Enabled || options.Cloud.Enabled
                ? $"Signing in to {harness.DisplayName}'s providers from Fleet isn't available when Fleet runs with sign-in."
                : $"Fleet can't sign in to {harness.DisplayName}'s providers.");
        }

        try
        {
            return await action(signIn).ConfigureAwait(false);
        }
        catch (HarnessSignInException ex)
        {
            return ex.NotFound
                ? new FleetError("SignIn.NotFound", ex.Message)
                : FleetError.ValidationError("SignIn.Refused", ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException or JsonException
                                   or TaskCanceledException { InnerException: TimeoutException })
        {
            // The exception's type only: Fleet's messages are safe, but a network error can quote what it sent.
            LogFailed(logger, harnessType, doing, ex.GetType().Name);
            return new FleetError("SignIn.Failed", ex is InvalidOperationException
                ? ex.Message
                : $"{harness.DisplayName} didn't answer when Fleet tried to {doing}. Try again.");
        }
    }

    private static FleetError? ValidateId(string? id, string what)
    {
        if (string.IsNullOrWhiteSpace(id))
            return FleetError.ValidationError("SignIn.Id", $"Say which {what}.");
        if (id.Length > MaxIdLength || id.Any(char.IsControl))
            return FleetError.ValidationError("SignIn.Id", $"That isn't a {what} Fleet knows.");
        return null;
    }

    private static FleetError? ValidateAnswers(IReadOnlyDictionary<string, JsonElement>? answers)
    {
        if (answers is null)
            return null;
        if (answers.Count > MaxAnswers || answers.Keys.Any(key => key.Length > MaxIdLength))
            return FleetError.ValidationError("SignIn.Answers", "Too many answers.");
        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't {Doing} for harness {HarnessType}: {ErrorType}")]
    private static partial void LogFailed(ILogger logger, string harnessType, string doing, string errorType);
}
