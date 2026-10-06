using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WeaveFleet.Infrastructure.Push;

/// <summary>
/// Message encryption for Web Push (RFC 8291) in the <c>aes128gcm</c> content encoding (RFC 8188): the only one
/// Apple's push service takes, and what every current browser expects. Built on .NET's own ECDH, HKDF and AES-GCM.
/// </summary>
public static class WebPushEncryption
{
    private const int RecordSize = 4096;
    private const int TagSize = 16;

    private static readonly byte[] KeyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
    private static readonly byte[] CekInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> for the browser holding <paramref name="userAgentPublicKey"/> (p256dh, 65
    /// bytes) and <paramref name="authSecret"/> (16 bytes). Returns the request body: header, then one record.
    /// <paramref name="salt"/> and <paramref name="serverKey"/> are made fresh unless a test fixes them.
    /// </summary>
    public static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        byte[] userAgentPublicKey,
        byte[] authSecret,
        byte[]? salt = null,
        ECDiffieHellman? serverKey = null)
    {
        if (userAgentPublicKey.Length != 65 || userAgentPublicKey[0] != 0x04)
            throw new ArgumentException("p256dh must be an uncompressed P-256 point (65 bytes).", nameof(userAgentPublicKey));
        if (authSecret.Length != 16)
            throw new ArgumentException("auth must be 16 bytes.", nameof(authSecret));
        if (plaintext.Length > RecordSize - TagSize - 1 - 86)
            throw new ArgumentException("The payload is too large for one record.", nameof(plaintext));

        salt ??= RandomNumberGenerator.GetBytes(16);
        using var ownKey = serverKey is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        var asKey = serverKey ?? ownKey!;

        var asPublic = PublicKeyBytes(asKey);
        using var uaKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = userAgentPublicKey[1..33], Y = userAgentPublicKey[33..65] },
        });
        var ecdhSecret = asKey.DeriveRawSecretAgreement(uaKey.PublicKey);

        // IKM = HKDF(auth_secret, ecdh_secret, "WebPush: info" || 0x00 || ua_public || as_public, 32)
        var keyInfo = Concat(KeyInfoPrefix, userAgentPublicKey, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);

        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, CekInfo);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo);

        // One record, so it's the last: the padding delimiter is 0x02.
        var padded = new byte[plaintext.Length + 1];
        plaintext.CopyTo(padded);
        padded[^1] = 0x02;

        var ciphertext = new byte[padded.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(cek, TagSize))
            aes.Encrypt(nonce, padded, ciphertext, tag);

        // Header: salt (16) || rs (4, big-endian) || idlen (1) || keyid (the server's public key).
        var body = new byte[16 + 4 + 1 + asPublic.Length + ciphertext.Length + TagSize];
        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16, 4), RecordSize);
        body[20] = (byte)asPublic.Length;
        asPublic.CopyTo(body, 21);
        ciphertext.CopyTo(body, 21 + asPublic.Length);
        tag.CopyTo(body, 21 + asPublic.Length + ciphertext.Length);
        return body;
    }

    internal static byte[] PublicKeyBytes(ECDiffieHellman key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        return Concat([0x04], parameters.Q.X!, parameters.Q.Y!);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
