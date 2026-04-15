using WeaveFleet.Application.Services;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeCredentialProtector : ICredentialProtector
{
    private const string Prefix = "ENC:";

    public string Encrypt(string plaintext) => $"{Prefix}{plaintext}";

    public string Decrypt(string ciphertext)
    {
        if (!ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"FakeCredentialProtector: value '{ciphertext}' was not encrypted by this fake.");

        return ciphertext[Prefix.Length..];
    }
}
