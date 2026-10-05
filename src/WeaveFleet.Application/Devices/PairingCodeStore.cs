using System.Security.Cryptography;
using System.Text;

namespace WeaveFleet.Application.Devices;

/// <summary>
/// One-time codes that let a phone pair with this machine. Each code has two forms: a long secret that rides in the
/// QR code's URL fragment, and an 8-character manual code someone can type (shown as <c>XXXX-XXXX</c>). Either form
/// redeems it, once, within <see cref="Lifetime"/>.
/// <para>
/// Codes live in memory only, hashed: a restart forgets them, which is fine for something that lasts ten minutes.
/// At most <see cref="MaxLive"/> are live at once; making another drops the oldest.
/// </para>
/// </summary>
public sealed class PairingCodeStore(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxLive = 5;

    // Crockford's base32: no I, L, O or U, so a typed code can't be misread.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int ManualCodeLength = 8;

    private readonly object _gate = new();
    private readonly List<Entry> _live = [];

    /// <summary>Makes a code. The secret and manual code are returned once; only their hashes are kept.</summary>
    public PairingCode Create()
    {
        Span<byte> secretBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(secretBytes);
        var secret = Convert.ToBase64String(secretBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var manual = new char[ManualCodeLength];
        for (var i = 0; i < manual.Length; i++)
            manual[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var manualCode = new string(manual);

        var expiresAt = time.GetUtcNow() + Lifetime;
        lock (_gate)
        {
            Purge();
            while (_live.Count >= MaxLive)
                _live.RemoveAt(0);
            _live.Add(new Entry(Hash(secret), Hash(manualCode), expiresAt));
        }

        return new PairingCode(secret, $"{manualCode[..4]}-{manualCode[4..]}", expiresAt);
    }

    /// <summary>The live code <paramref name="secret"/> or <paramref name="manualCode"/> names, without using it up.</summary>
    public PairingTicket? Peek(string? secret, string? manualCode)
    {
        lock (_gate)
        {
            Purge();
            var entry = Find(secret, manualCode);
            return entry is null ? null : new PairingTicket(entry.ExpiresAt);
        }
    }

    /// <summary>Uses up the code. Only one caller gets it; every later attempt finds nothing.</summary>
    public PairingTicket? TryConsume(string? secret, string? manualCode)
    {
        lock (_gate)
        {
            Purge();
            var entry = Find(secret, manualCode);
            if (entry is null)
                return null;

            _live.Remove(entry);
            return new PairingTicket(entry.ExpiresAt);
        }
    }

    /// <summary>
    /// A typed code as Fleet compares it: upper case, without the dash or spaces, with the letters people confuse
    /// for digits read as those digits. Null when it can't be a code.
    /// </summary>
    public static string? NormalizeManualCode(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return null;

        var builder = new StringBuilder(ManualCodeLength);
        foreach (var raw in typed)
        {
            if (raw is '-' || char.IsWhiteSpace(raw))
                continue;

            var c = char.ToUpperInvariant(raw) switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                var other => other,
            };
            if (Alphabet.IndexOf(c) < 0 || builder.Length == ManualCodeLength)
                return null;
            builder.Append(c);
        }

        return builder.Length == ManualCodeLength ? builder.ToString() : null;
    }

    private Entry? Find(string? secret, string? manualCode)
    {
        byte[]? secretHash = string.IsNullOrEmpty(secret) || secret.Length > 128 ? null : Hash(secret);
        byte[]? codeHash = NormalizeManualCode(manualCode) is { } code ? Hash(code) : null;
        if (secretHash is null && codeHash is null)
            return null;

        Entry? found = null;
        foreach (var entry in _live)
        {
            // Compare every entry in constant time, so how long a guess takes says nothing about the live codes.
            var matches = (secretHash is not null && CryptographicOperations.FixedTimeEquals(secretHash, entry.SecretHash))
                | (codeHash is not null && CryptographicOperations.FixedTimeEquals(codeHash, entry.CodeHash));
            if (matches && found is null)
                found = entry;
        }

        return found;
    }

    private void Purge()
    {
        var now = time.GetUtcNow();
        _live.RemoveAll(entry => entry.ExpiresAt <= now);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private sealed record Entry(byte[] SecretHash, byte[] CodeHash, DateTimeOffset ExpiresAt);
}

/// <summary>A new pairing code, as the computer shows it.</summary>
/// <param name="Secret">32 random bytes, base64url: goes in the QR code's URL fragment.</param>
/// <param name="ManualCode">The typeable form, <c>XXXX-XXXX</c>.</param>
public sealed record PairingCode(string Secret, string ManualCode, DateTimeOffset ExpiresAt);

/// <summary>A live pairing code that was found.</summary>
public sealed record PairingTicket(DateTimeOffset ExpiresAt);
