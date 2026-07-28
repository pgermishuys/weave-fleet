using System.Security.Cryptography;
using System.Text;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

/// <summary>
/// Computes the credential-boundary key for pooled OpenCode processes.
/// </summary>
/// <remarks>
/// Threat model: pooled OpenCode environment variables can contain decrypted credential values.
/// This type never stores those values in fields, caches, closures, or returned objects. Values are
/// read from the caller-owned authoritative environment dictionary only long enough to encode and
/// append them to the SHA256 input. Temporary byte buffers are cleared before the method returns
/// where the runtime permits; caller-owned strings/dictionaries remain the caller's responsibility.
/// The returned hash is the only value retained by pooling code for equality/comparison, so two
/// credential boundaries can share a process only when the full resolved environment hashes match.
/// </remarks>
internal static class CredentialHasher
{
    public static string HashEnvironment(IReadOnlyDictionary<string, string> environmentVariables)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in environmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AddHashPart(hash, pair.Key);
            AddHashPart(hash, pair.Value);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AddHashPart(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var lengthBytes = BitConverter.GetBytes(bytes.Length);
        try
        {
            hash.AppendData(lengthBytes);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(lengthBytes);
        }
    }
}
