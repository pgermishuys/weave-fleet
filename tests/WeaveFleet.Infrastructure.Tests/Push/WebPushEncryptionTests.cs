using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Push;

namespace WeaveFleet.Infrastructure.Tests.Push;

/// <summary>Web Push message encryption, aes128gcm (RFC 8291 over RFC 8188).</summary>
public sealed class WebPushEncryptionTests
{
    // RFC 8291, section 5: the worked example.
    private const string Plaintext = "When I grow up, I want to be a watermelon";
    private const string ServerPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string ServerPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string Expected = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    [Fact]
    public void Matches_the_rfc_8291_example()
    {
        using var serverKey = Key(ServerPrivate, ServerPublic);

        var body = WebPushEncryption.Encrypt(
            Encoding.UTF8.GetBytes(Plaintext),
            Base64Url.Decode(UaPublic),
            Base64Url.Decode(AuthSecret),
            Base64Url.Decode(Salt),
            serverKey);

        Base64Url.Encode(body).ShouldBe(Expected);
    }

    [Fact]
    public void The_browser_can_decrypt_a_fresh_message()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaPublic = PublicBytes(browser);
        var auth = RandomNumberGenerator.GetBytes(16);
        var payload = """{"v":1,"title":"Needs you"}""";

        var body = WebPushEncryption.Encrypt(Encoding.UTF8.GetBytes(payload), uaPublic, auth);

        Decrypt(body, browser, uaPublic, auth).ShouldBe(payload);
        BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)).ShouldBe(4096u);
        body[20].ShouldBe((byte)65);
    }

    [Fact]
    public void Each_message_uses_a_fresh_salt_and_key()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaPublic = PublicBytes(browser);
        var auth = RandomNumberGenerator.GetBytes(16);

        var first = WebPushEncryption.Encrypt("same"u8, uaPublic, auth);
        var second = WebPushEncryption.Encrypt("same"u8, uaPublic, auth);

        first.AsSpan(0, 86).SequenceEqual(second.AsSpan(0, 86)).ShouldBeFalse();
    }

    [Fact]
    public void Bad_keys_are_refused()
    {
        Should.Throw<ArgumentException>(() => WebPushEncryption.Encrypt("x"u8, new byte[33], new byte[16]));
        Should.Throw<ArgumentException>(() => WebPushEncryption.Encrypt("x"u8, Base64Url.Decode(UaPublic), new byte[8]));
    }

    /// <summary>What the browser does (RFC 8291 from the receiving side), to check a message round-trips.</summary>
    private static string Decrypt(byte[] body, ECDiffieHellman browser, byte[] uaPublic, byte[] auth)
    {
        var salt = body[..16];
        var idLength = body[20];
        var serverPublic = body[21..(21 + idLength)];
        var record = body[(21 + idLength)..];

        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] },
        });
        var ecdh = browser.DeriveRawSecretAgreement(server.PublicKey);
        var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(uaPublic).Concat(serverPublic).ToArray();
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, auth, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var plain = new byte[record.Length - 16];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, record[..^16], record[^16..], plain);
        plain[^1].ShouldBe((byte)2);
        return Encoding.UTF8.GetString(plain[..^1]);
    }

    private static ECDiffieHellman Key(string privateKey, string publicKey)
    {
        var q = Base64Url.Decode(publicKey);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.Decode(privateKey),
            Q = new ECPoint { X = q[1..33], Y = q[33..65] },
        });
    }

    private static byte[] PublicBytes(ECDiffieHellman key)
    {
        var p = key.ExportParameters(false);
        return [0x04, .. p.Q.X!, .. p.Q.Y!];
    }
}
