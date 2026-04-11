namespace WeaveFleet.Application.Services;

/// <summary>
/// Application-layer interface for encrypting and decrypting credential values.
/// Implementations should use ASP.NET Core Data Protection with a persisted key ring.
/// </summary>
public interface ICredentialProtector
{
    /// <summary>Encrypt a plaintext credential value. Returns a ciphertext string safe to persist.</summary>
    string Encrypt(string plaintext);

    /// <summary>Decrypt a ciphertext credential value previously encrypted by <see cref="Encrypt"/>.</summary>
    string Decrypt(string ciphertext);
}
