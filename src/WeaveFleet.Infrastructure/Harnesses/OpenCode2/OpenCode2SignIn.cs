using System.Collections.Concurrent;
using System.Text.Json;
using System.Web;
using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// OpenCode 2's provider sign-in over its integration API (<c>/api/integration</c>, <c>/api/credential</c>): what
/// <c>opencode2 auth login</c> does, from Fleet's Settings.
/// </summary>
/// <remarks>
/// <para>
/// Sign-ins live in V2's database, which every server of the install shares, profile servers too, and a server sees
/// another's changes at once (checked on 2.0.9). So every request goes to the owner's server without a profile, the
/// one that stays up; a browser sign-in is held in that server's memory, so its later requests must go there too.
/// </para>
/// <para>
/// V2 keeps integrations, and the browser sign-ins under way, per location, and a location's integrations are
/// registered only once it has loaded (a sign-in started on a server that just started fails). So every request names
/// one folder of Fleet's own, with nothing in it but the user's and Fleet's config, and waits for it to load.
/// </para>
/// <para>
/// A browser sign-in either finishes on its own (V2 polls the provider, or a provider sends the browser back to a
/// listener V2 opened on <c>localhost</c>) or asks for a code. A listener on <c>localhost</c> is on the machine Fleet
/// runs on, which a browser on another device can't reach: the attempt says so (<see cref="HarnessSignInAttempt.CallbackAddress"/>),
/// and <see cref="ForwardCallbackAsync"/> takes the address that browser landed on and passes its query to the
/// listener from here.
/// </para>
/// </remarks>
internal sealed class OpenCode2SignIn(
    Func<string, CancellationToken, Task<OpenCode2Server>> ownerServer,
    Func<string> folder,
    Func<OpenCode2InstallMode> installMode,
    Func<HttpClient> callbackClient,
    TimeProvider timeProvider) : IHarnessProviderSignIn
{
    /// <summary>How long V2 keeps a browser sign-in open (<c>attemptLifetime</c> in V2's integration service).</summary>
    internal static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<(string Owner, string AttemptId), PendingCallback> _callbacks = new();

    private sealed record PendingCallback(string ProviderId, Uri Address, DateTimeOffset Expires);

    public async Task<HarnessSignIns> ListAsync(string ownerUserId, CancellationToken ct)
    {
        var (server, location) = await ServerAsync(ownerUserId, ct).ConfigureAwait(false);
        var integrations = await server.Client.GetIntegrationsAsync(location, ct).ConfigureAwait(false);
        var providers = integrations
            .Where(integration => !string.IsNullOrWhiteSpace(integration.Id))
            .Select(ToProvider)
            .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new HarnessSignIns(providers, Note(installMode()));
    }

    public async Task SignInWithKeyAsync(
        string ownerUserId,
        string providerId,
        string key,
        IReadOnlyDictionary<string, JsonElement> answers,
        CancellationToken ct)
    {
        var (server, location) = await ServerAsync(ownerUserId, ct).ConfigureAwait(false);
        try
        {
            await server.Client.ConnectKeyAsync(location, providerId, key, answers, ct).ConfigureAwait(false);
        }
        catch (HarnessSignInException ex) when (ex.Message.Contains(key, StringComparison.Ordinal))
        {
            // V2's messages name fields, not values; should one ever quote the key, it goes no further.
            throw new HarnessSignInException("OpenCode 2 didn't accept that key.", ex.NotFound);
        }
    }

    public async Task<HarnessSignInAttempt> StartAsync(
        string ownerUserId,
        string providerId,
        string methodId,
        IReadOnlyDictionary<string, JsonElement> answers,
        CancellationToken ct)
    {
        ForgetExpired();
        var (server, location) = await ServerAsync(ownerUserId, ct).ConfigureAwait(false);
        var started = await server.Client.StartOAuthAsync(location, providerId, methodId, answers, ct).ConfigureAwait(false);

        var expires = ReadTime(started.Time?.Expires) ?? timeProvider.GetUtcNow() + AttemptLifetime;
        var callback = LoopbackCallback(started.Url!);
        if (callback is not null)
            _callbacks[(ownerUserId, started.AttemptId!)] = new PendingCallback(providerId, callback, expires);

        return new HarnessSignInAttempt(
            started.AttemptId!,
            started.Url!,
            started.Instructions ?? string.Empty,
            NeedsCode: string.Equals(started.Mode, "code", StringComparison.Ordinal),
            expires)
        {
            CallbackAddress = callback?.GetLeftPart(UriPartial.Path),
        };
    }

    public async Task<HarnessSignInAttemptStatus> GetAttemptAsync(string ownerUserId, string providerId, string attemptId, CancellationToken ct)
    {
        var (server, location) = await ServerAsync(ownerUserId, ct).ConfigureAwait(false);
        var status = await server.Client.GetOAuthStatusAsync(location, providerId, attemptId, ct).ConfigureAwait(false);
        var state = status?.Status ?? HarnessSignInAttemptStates.Gone;
        if (state != HarnessSignInAttemptStates.Pending)
            _callbacks.TryRemove((ownerUserId, attemptId), out _);
        return new HarnessSignInAttemptStatus(state, state == HarnessSignInAttemptStates.Failed ? status?.Message : null);
    }

    public async Task SubmitCodeAsync(string ownerUserId, string providerId, string attemptId, string code, CancellationToken ct)
    {
        var (server, location) = await ServerAsync(ownerUserId, ct).ConfigureAwait(false);
        await server.Client.CompleteOAuthAsync(location, providerId, attemptId, code, ct).ConfigureAwait(false);
    }

    /// <remarks>
    /// Only the query of <paramref name="landedOn"/> is used, and it goes only to the address V2 itself gave the
    /// provider for this attempt, so what the user pastes can't send Fleet anywhere else. The address must be the
    /// callback's own path and port, since a listener that gets the wrong query fails the sign-in.
    /// </remarks>
    public async Task ForwardCallbackAsync(string ownerUserId, string providerId, string attemptId, Uri landedOn, CancellationToken ct)
    {
        if (!_callbacks.TryGetValue((ownerUserId, attemptId), out var pending)
            || !string.Equals(pending.ProviderId, providerId, StringComparison.Ordinal)
            || pending.Expires <= timeProvider.GetUtcNow())
        {
            throw new HarnessSignInException("This sign-in isn't waiting for the browser any more. Start it again.", notFound: true);
        }

        if (landedOn.Port != pending.Address.Port
            || !string.Equals(landedOn.AbsolutePath, pending.Address.AbsolutePath, StringComparison.Ordinal))
        {
            throw new HarnessSignInException(
                $"That isn't the page the provider sent you to. Its address starts with {pending.Address.GetLeftPart(UriPartial.Path)}.");
        }

        var target = new UriBuilder(pending.Address) { Query = landedOn.Query.TrimStart('?') }.Uri;
        using var client = callbackClient();
        try
        {
            // The listener answers with a page saying how it went; the attempt's status says the same, so it isn't read.
            using var response = await client.GetAsync(target, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new HarnessSignInException(
                $"Nothing is waiting at {pending.Address.GetLeftPart(UriPartial.Path)} any more. Start the sign-in again.");
        }
    }

    public async Task CancelAsync(string ownerUserId, string providerId, string attemptId, CancellationToken ct)
    {
        _callbacks.TryRemove((ownerUserId, attemptId), out _);
        var (server, location) = await ServerAsync(ownerUserId, ct).ConfigureAwait(false);
        await server.Client.CancelOAuthAsync(location, providerId, attemptId, ct).ConfigureAwait(false);
    }

    public async Task UseAsync(string ownerUserId, string connectionId, CancellationToken ct)
    {
        var server = await ownerServer(ownerUserId, ct).ConfigureAwait(false);
        await server.Client.ActivateCredentialAsync(connectionId, ct).ConfigureAwait(false);
    }

    public async Task SignOutAsync(string ownerUserId, string connectionId, CancellationToken ct)
    {
        var server = await ownerServer(ownerUserId, ct).ConfigureAwait(false);
        await server.Client.RemoveCredentialAsync(connectionId, ct).ConfigureAwait(false);
    }

    /// <summary>The owner's server, with sign-in's folder loaded on it.</summary>
    private async Task<(OpenCode2Server Server, string Location)> ServerAsync(string ownerUserId, CancellationToken ct)
    {
        var server = await ownerServer(ownerUserId, ct).ConfigureAwait(false);
        var location = folder();
        await server.LoadLocationAsync(location, ct).ConfigureAwait(false);
        return (server, location);
    }

    /// <summary>Where the install's sign-ins are kept, and what signing in here changes.</summary>
    internal static string Note(OpenCode2InstallMode mode) => mode == OpenCode2InstallMode.Separate
        ? "These sign-ins belong to Fleet's separate OpenCode 2 install and are kept in its own database. OpenCode 1's sign-ins don't carry over, and signing in here doesn't change OpenCode 1."
        : "OpenCode 2 uses its own default database here, so signing in changes your OpenCode 2 install, the same as running opencode2 auth login in a terminal.";

    internal static HarnessSignInProvider ToProvider(OpenCode2Integration integration)
    {
        var methods = (integration.Methods ?? []).Select(ToMethod).OfType<HarnessSignInMethod>().ToList();

        // V2 lists the stored sign-ins first, the one in use first, then the environment variables it found set; a
        // provider uses a stored sign-in over the environment.
        var connections = new List<HarnessSignInConnection>();
        foreach (var connection in integration.Connections ?? [])
        {
            var active = connections.Count == 0;
            if (connection.Type == HarnessSignInConnectionKinds.Credential && connection.Id is { Length: > 0 } id)
                connections.Add(new HarnessSignInConnection(HarnessSignInConnectionKinds.Credential, id, connection.Label ?? id, active));
            else if (connection.Type == HarnessSignInConnectionKinds.Environment && connection.Name is { Length: > 0 } name)
                connections.Add(new HarnessSignInConnection(HarnessSignInConnectionKinds.Environment, name, name, active));
        }

        return new HarnessSignInProvider(integration.Id!, integration.Name ?? integration.Id!, methods, connections);
    }

    private static HarnessSignInMethod? ToMethod(OpenCode2IntegrationMethod method) => method.Type switch
    {
        HarnessSignInMethodTypes.Key => new HarnessSignInMethod(HarnessSignInMethodTypes.Key, null, method.Label ?? "API key")
        {
            Fields = ToFields(method.Form),
        },
        HarnessSignInMethodTypes.OAuth when method.Id is { Length: > 0 } id => new HarnessSignInMethod(HarnessSignInMethodTypes.OAuth, id, method.Label ?? id)
        {
            Fields = ToFields(method.Form),
        },
        HarnessSignInMethodTypes.Command when method.Id is { Length: > 0 } id && method.Command is { Count: > 0 } command =>
            new HarnessSignInMethod(HarnessSignInMethodTypes.Command, id, method.Label ?? id) { Command = command },
        HarnessSignInMethodTypes.Environment when method.Names is { Count: > 0 } names =>
            new HarnessSignInMethod(HarnessSignInMethodTypes.Environment, null, "Environment variable") { EnvironmentVariables = names },
        _ => null,
    };

    private static List<HarnessSignInField> ToFields(IReadOnlyList<OpenCode2IntegrationField>? form)
        => (form ?? [])
            .Where(field => field.Key is { Length: > 0 } && field.Type is { Length: > 0 })
            .Select(field => new HarnessSignInField(field.Key!, field.Type!)
            {
                Title = field.Title,
                Description = field.Description,
                Required = field.Required == true,
                Hidden = field.Hidden == true,
                Placeholder = field.Placeholder,
                Default = field.Default is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } value ? value : null,
                Options = field.Options?
                    .Where(option => option.Value is not null)
                    .Select(option => new HarnessSignInOption(option.Value!, option.Label ?? option.Value!, option.Description))
                    .ToList(),
                When = field.When?
                    .Where(condition => condition.Key is { Length: > 0 } && condition.Op is "eq" or "neq")
                    .Select(condition => new HarnessSignInCondition(condition.Key!, condition.Op!, condition.Value))
                    .ToList(),
                Url = field.Url,
            })
            .ToList();

    /// <summary>
    /// The address a sign-in page sends the browser back to (its <c>redirect_uri</c>), when that's a listener on this
    /// machine rather than a website.
    /// </summary>
    internal static Uri? LoopbackCallback(string signInUrl)
    {
        if (!Uri.TryCreate(signInUrl, UriKind.Absolute, out var url))
            return null;

        var redirect = HttpUtility.ParseQueryString(url.Query)["redirect_uri"];
        if (!Uri.TryCreate(redirect, UriKind.Absolute, out var callback) || callback.Scheme != Uri.UriSchemeHttp)
            return null;

        return callback.IsLoopback || string.Equals(callback.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(callback) { Query = string.Empty, Fragment = string.Empty }.Uri
            : null;
    }

    private static DateTimeOffset? ReadTime(JsonElement? milliseconds)
        => milliseconds is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    private void ForgetExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (key, pending) in _callbacks)
        {
            if (pending.Expires <= now)
                _callbacks.TryRemove(key, out _);
        }
    }
}
