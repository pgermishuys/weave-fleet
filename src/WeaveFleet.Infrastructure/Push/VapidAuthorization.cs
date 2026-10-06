using System.Security.Cryptography;
using System.Text;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Infrastructure.Push;

/// <summary>
/// The <c>Authorization: vapid t=…, k=…</c> header (RFC 8292): a JWT signed with this machine's VAPID key, for the
/// push service's origin, and the public key it verifies with.
/// </summary>
public static class VapidAuthorization
{
    /// <summary>How long a signed header is good for. Push services accept up to 24 hours.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public static string Create(Uri endpoint, string subject, VapidKeys keys, DateTimeOffset now)
    {
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var expires = (now + Lifetime).ToUnixTimeSeconds();

        var header = Base64Url.Encode("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = Base64Url.Encode(Encoding.UTF8.GetBytes(
            $$"""{"aud":{{Quote(audience)}},"exp":{{expires}},"sub":{{Quote(subject)}}}"""));
        var signingInput = $"{header}.{claims}";

        var publicKey = Base64Url.Decode(keys.PublicKey);
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.Decode(keys.PrivateKey),
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"vapid t={signingInput}.{Base64Url.Encode(signature)}, k={keys.PublicKey}";
    }

    private static string Quote(string value) => $"\"{System.Text.Json.JsonEncodedText.Encode(value)}\"";
}
