using System.Security.Cryptography;
using System.Text;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Provides a process-local bearer token for lightweight local authentication.
/// </summary>
public interface ILocalTokenAuthService
{
    /// <summary>
    /// Gets the configured or generated authentication token.
    /// </summary>
    string Token { get; }

    /// <summary>
    /// Validates a candidate token using constant-time comparison.
    /// </summary>
    bool ValidateToken(string candidate);
}

/// <summary>
/// Provides a singleton local authentication token sourced from environment configuration or generated securely at startup.
/// </summary>
public sealed class LocalTokenAuthService : ILocalTokenAuthService
{
    private const string AuthTokenEnvironmentVariable = "WEAVE_FLEET_AUTH_TOKEN";
    private static readonly Encoding Utf8 = Encoding.UTF8;

    private readonly byte[] _tokenBytes;

    public LocalTokenAuthService()
    {
        var configuredToken = Environment.GetEnvironmentVariable(AuthTokenEnvironmentVariable);
        Token = !string.IsNullOrWhiteSpace(configuredToken) && configuredToken.Length >= 16
            ? configuredToken
            : GenerateToken();

        _tokenBytes = Utf8.GetBytes(Token);
    }

    public string Token { get; }

    public bool ValidateToken(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var candidateBytes = Utf8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(candidateBytes, _tokenBytes);
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }
}
