using System.Security.Cryptography;
using System.Text;

namespace WeaveFleet.Domain.Identity;

/// <summary>
/// Generates ascending message IDs with format: msg_[12 hex chars][14 base62 chars]
/// Total length: 30 characters
/// Algorithm: (timestampMs * 0x1000 + counter) → lower 6 bytes → big-endian hex
/// </summary>
public static class AscendingMessageId
{
    private const string Prefix = "msg_";
    private const int HexLength = 12;
    private const int RandomLength = 14;
    private const string Base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    
    private static readonly object Lock = new();
    private static long _lastTimestamp;
    private static int _counter;

    /// <summary>
    /// Generates a new ascending message ID.
    /// </summary>
    public static string New()
    {
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int counter;

        lock (Lock)
        {
            if (timestampMs > _lastTimestamp)
            {
                _lastTimestamp = timestampMs;
                _counter = 0;
            }
            else if (timestampMs == _lastTimestamp)
            {
                _counter++;
            }
            else
            {
                // Clock moved backwards - use last timestamp and increment counter
                timestampMs = _lastTimestamp;
                _counter++;
            }

            counter = _counter;
        }

        return NewFromTimestamp(timestampMs, counter);
    }

    /// <summary>
    /// Generates a message ID from a specific timestamp and counter.
    /// Used for testing and deterministic ID generation.
    /// </summary>
    public static string NewFromTimestamp(long timestampMs, int counter)
    {
        // Algorithm: (timestampMs * 0x1000 + counter) → lower 6 bytes → big-endian hex
        var combined = (timestampMs * 0x1000L) + counter;
        
        // Take lower 6 bytes (48 bits) in big-endian order
        var bytes = new byte[6];
        bytes[5] = (byte)(combined & 0xFF);
        bytes[4] = (byte)((combined >> 8) & 0xFF);
        bytes[3] = (byte)((combined >> 16) & 0xFF);
        bytes[2] = (byte)((combined >> 24) & 0xFF);
        bytes[1] = (byte)((combined >> 32) & 0xFF);
        bytes[0] = (byte)((combined >> 40) & 0xFF);

        // Convert to hex (12 characters)
        var hexPart = Convert.ToHexStringLower(bytes);

        // Generate 14 random base62 characters
        var randomPart = GenerateBase62(RandomLength);

        return $"{Prefix}{hexPart}{randomPart}";
    }

    /// <summary>
    /// Extracts the original timestamp (in milliseconds) from a message ID.
    /// </summary>
    public static long ExtractTimestamp(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith(Prefix, StringComparison.Ordinal) || id.Length < Prefix.Length + HexLength)
        {
            throw new ArgumentException($"Invalid message ID format: {id}", nameof(id));
        }

        // Extract hex part (12 characters after prefix)
        var hexPart = id.Substring(Prefix.Length, HexLength);
        
        // Convert hex to bytes (6 bytes)
        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = Convert.ToByte(hexPart.Substring(i * 2, 2), 16);
        }

        // Reconstruct the combined value (big-endian)
        long combined = 0;
        for (var i = 0; i < 6; i++)
        {
            combined = (combined << 8) | bytes[i];
        }

        // Extract timestamp: combined / 0x1000
        return combined / 0x1000;
    }

    private static string GenerateBase62(int length)
    {
        var result = new StringBuilder(length);
        var bytes = new byte[length];
        
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        for (var i = 0; i < length; i++)
        {
            result.Append(Base62Alphabet[bytes[i] % Base62Alphabet.Length]);
        }

        return result.ToString();
    }
}
