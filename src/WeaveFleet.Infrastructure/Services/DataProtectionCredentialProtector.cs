using Microsoft.AspNetCore.DataProtection;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// <see cref="ICredentialProtector"/> implementation backed by ASP.NET Core Data Protection.
/// Uses a dedicated purpose string ("UserCredentials") for key isolation.
/// Keys must be persisted via <c>PersistKeysToFileSystem</c> in Program.cs for
/// encrypted credentials to survive process restarts.
/// </summary>
internal sealed class DataProtectionCredentialProtector : ICredentialProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionCredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("UserCredentials");
    }

    /// <inheritdoc />
    public string Encrypt(string plaintext) => _protector.Protect(plaintext);

    /// <inheritdoc />
    public string Decrypt(string ciphertext) => _protector.Unprotect(ciphertext);
}
