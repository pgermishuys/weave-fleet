using System.Security.Cryptography;
using System.Text;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Application.Services;

/// <summary>Where the local access token came from.</summary>
public enum LocalTokenSource
{
    /// <summary>Fleet made it and keeps it next to the database; it survives restarts and can be replaced.</summary>
    Saved,

    /// <summary><c>WEAVE_FLEET_AUTH_TOKEN</c> fixes it; only changing the variable changes it.</summary>
    Environment,

    /// <summary>Made for this run only and forgotten when Fleet stops.</summary>
    Ephemeral,
}

/// <summary>
/// Provides the local bearer token for lightweight local authentication.
/// </summary>
public interface ILocalTokenAuthService
{
    /// <summary>
    /// Gets the configured or generated authentication token.
    /// </summary>
    string Token { get; }

    /// <summary>Where <see cref="Token"/> came from.</summary>
    LocalTokenSource Source => LocalTokenSource.Ephemeral;

    /// <summary>
    /// Validates a candidate token using constant-time comparison.
    /// </summary>
    bool ValidateToken(string candidate);

    /// <summary>
    /// Replaces the token, so every device holding the old one loses access. False when the environment fixes it.
    /// </summary>
    bool TryReplaceToken(out string token)
    {
        token = Token;
        return false;
    }
}

/// <summary>
/// The local access token: <c>WEAVE_FLEET_AUTH_TOKEN</c> when set, otherwise the one saved with this machine's
/// identity, so a device that was given it keeps working after Fleet restarts.
/// </summary>
public sealed class LocalTokenAuthService : ILocalTokenAuthService
{
    private const string AuthTokenEnvironmentVariable = "WEAVE_FLEET_AUTH_TOKEN";
    private static readonly Encoding Utf8 = Encoding.UTF8;

    private readonly MachineIdentityStore? _store;
    private volatile TokenState _state;

    /// <summary>A token for this run only. Tests and hosts without a data directory use it.</summary>
    public LocalTokenAuthService()
        : this(store: null)
    {
    }

    public LocalTokenAuthService(MachineIdentityStore? store)
    {
        _store = store;

        var configuredToken = System.Environment.GetEnvironmentVariable(AuthTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredToken) && configuredToken.Length >= MachineIdentityStore.MinimumTokenLength)
            _state = new TokenState(configuredToken, LocalTokenSource.Environment);
        else if (store is not null)
            _state = new TokenState(store.Get().AccessToken!, LocalTokenSource.Saved);
        else
            _state = new TokenState(MachineIdentityStore.NewToken(), LocalTokenSource.Ephemeral);
    }

    public string Token => _state.Token;

    public LocalTokenSource Source => _state.Source;

    public bool ValidateToken(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var candidateBytes = Utf8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(candidateBytes, _state.Bytes);
    }

    public bool TryReplaceToken(out string token)
    {
        var current = _state;
        if (current.Source == LocalTokenSource.Environment)
        {
            token = current.Token;
            return false;
        }

        var next = MachineIdentityStore.NewToken();
        _store?.Update(identity => identity with { AccessToken = next });
        _state = new TokenState(next, current.Source);
        token = next;
        return true;
    }

    private sealed class TokenState(string token, LocalTokenSource source)
    {
        public string Token { get; } = token;
        public LocalTokenSource Source { get; } = source;
        public byte[] Bytes { get; } = Utf8.GetBytes(token);
    }
}
