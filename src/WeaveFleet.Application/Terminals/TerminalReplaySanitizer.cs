using System.Buffers;

namespace WeaveFleet.Application.Terminals;

/// <summary>
/// Removes terminal queries and their replies from output before it's saved for replay. Replaying a stored
/// query makes the browser's terminal answer it again, and the shell prints the answer as junk at the prompt.
/// Colours, cursor movement, titles and everything else pass through unchanged.
/// </summary>
/// <remarks>
/// Works on raw bytes and keeps state between chunks, because a sequence can arrive split across two reads.
/// Only the 7-bit forms (<c>ESC [</c>, <c>ESC ]</c>, <c>ESC P</c>, …) are recognised: in UTF-8 a lone 0x9B
/// byte is part of an ordinary character, not a control code. The rules follow t3code's terminal manager.
/// Not thread-safe.
/// </remarks>
public sealed class TerminalReplaySanitizer
{
    private const byte Esc = 0x1B;
    private const byte Bel = 0x07;

    /// <summary>An unfinished sequence longer than this is treated as text rather than held forever.</summary>
    private const int MaxPending = 4096;

    private byte[] _pending = [];

    /// <summary>Returns the bytes of <paramref name="data"/> to keep, holding back an unfinished sequence.</summary>
    public byte[] Process(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> input;
        byte[]? joined = null;
        if (_pending.Length == 0)
        {
            input = data;
        }
        else
        {
            joined = new byte[_pending.Length + data.Length];
            _pending.CopyTo(joined, 0);
            data.CopyTo(joined.AsSpan(_pending.Length));
            input = joined;
        }

        var output = new ArrayBufferWriter<byte>(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            if (input[i] != Esc)
            {
                var next = input[i..].IndexOf(Esc);
                var end = next < 0 ? input.Length : i + next;
                output.Write(input[i..end]);
                i = end;
                continue;
            }

            var consumed = ReadEscape(input, i, output);
            if (consumed < 0)
            {
                // Unfinished: keep it for the next chunk, unless it's grown too long to be a real sequence.
                var rest = input[i..];
                if (rest.Length > MaxPending)
                {
                    output.Write(rest);
                    _pending = [];
                }
                else
                {
                    _pending = rest.ToArray();
                }
                return output.WrittenSpan.ToArray();
            }
            i += consumed;
        }

        _pending = [];
        return output.WrittenSpan.ToArray();
    }

    /// <summary>Returns whatever is held back, as is. Call when the terminal ends.</summary>
    public byte[] Flush()
    {
        var rest = _pending;
        _pending = [];
        return rest;
    }

    /// <summary>
    /// Handles the escape sequence starting at <paramref name="start"/>. Writes it to
    /// <paramref name="output"/> unless it should be stripped, and returns how many bytes it took, or -1 when
    /// the input ends before the sequence does.
    /// </summary>
    private static int ReadEscape(ReadOnlySpan<byte> input, int start, ArrayBufferWriter<byte> output)
    {
        if (start + 1 >= input.Length)
            return -1;

        var kind = input[start + 1];
        switch (kind)
        {
            case (byte)'[':
            {
                for (var cursor = start + 2; cursor < input.Length; cursor++)
                {
                    var b = input[cursor];
                    if (b is >= 0x40 and <= 0x7E)
                    {
                        if (!ShouldStripCsi(input[(start + 2)..cursor], b))
                            output.Write(input[start..(cursor + 1)]);
                        return cursor + 1 - start;
                    }
                    if (b < 0x20 || b > 0x7E)
                    {
                        // Not a well-formed CSI. Keep what we have and carry on from the odd byte.
                        output.Write(input[start..cursor]);
                        return cursor - start;
                    }
                }
                return -1;
            }

            case (byte)']': // OSC
            case (byte)'P': // DCS
            case (byte)'^': // PM
            case (byte)'_': // APC
            {
                var (contentEnd, sequenceEnd) = FindStringTerminator(input, start + 2);
                if (sequenceEnd < 0)
                    return -1;

                var content = input[(start + 2)..contentEnd];
                var strip = (kind == (byte)']' && ShouldStripOsc(content))
                    || (kind == (byte)'P' && ShouldStripDcs(content));
                if (!strip)
                    output.Write(input[start..sequenceEnd]);
                return sequenceEnd - start;
            }

            default:
            {
                // ESC, optional intermediates (0x20–0x2F), then a final byte (0x30–0x7E).
                var cursor = start + 1;
                while (cursor < input.Length && input[cursor] is >= 0x20 and <= 0x2F)
                    cursor++;
                if (cursor >= input.Length)
                    return -1;

                var end = input[cursor] is >= 0x30 and <= 0x7E ? cursor + 1 : start + 1;
                output.Write(input[start..end]);
                return end - start;
            }
        }
    }

    /// <summary>Finds BEL, ESC \ or the UTF-8 encoded ST (C2 9C). Returns where the content ends and the sequence ends.</summary>
    private static (int ContentEnd, int SequenceEnd) FindStringTerminator(ReadOnlySpan<byte> input, int from)
    {
        for (var i = from; i < input.Length; i++)
        {
            var b = input[i];
            if (b == Bel)
                return (i, i + 1);
            if (b == Esc)
            {
                if (i + 1 >= input.Length)
                    return (-1, -1);
                if (input[i + 1] == (byte)'\\')
                    return (i, i + 2);
            }
            if (b == 0xC2)
            {
                if (i + 1 >= input.Length)
                    return (-1, -1);
                if (input[i + 1] == 0x9C)
                    return (i, i + 2);
            }
        }
        return (-1, -1);
    }

    private static bool ShouldStripCsi(ReadOnlySpan<byte> body, byte final) => final switch
    {
        // Device status report and cursor position requests.
        (byte)'n' => true,
        // Cursor position report.
        (byte)'R' => AllOf(body, "0123456789;?"),
        // Device attributes, primary and secondary.
        (byte)'c' => AllOf(body, ">0123456789;?"),
        // DECRQM mode queries (…$p) and DECRPM replies (…$y). DECSTR (!p) and DECSCL ("p) stay.
        (byte)'p' or (byte)'y' => body.Length > 0 && body[^1] == (byte)'$' && AllOf(body[..^1], "0123456789;?"),
        // XTVERSION (>q). DECSCUSR (space, then q) stays.
        (byte)'q' => body.Length > 0 && body[0] == (byte)'>' && AllOf(body[1..], "0123456789;"),
        // Kitty keyboard protocol query and reply (?u). Restore cursor (bare u) stays.
        (byte)'u' => body.Length > 0 && body[0] == (byte)'?',
        _ => false,
    };

    /// <summary>DECRQSS ($q) and XTGETTCAP (+q) queries and their replies ([01]$r, [01]+r).</summary>
    private static bool ShouldStripDcs(ReadOnlySpan<byte> content)
    {
        var i = 0;
        if (i < content.Length && content[i] is (byte)'0' or (byte)'1')
            i++;
        return i + 1 < content.Length
            && content[i] is (byte)'$' or (byte)'+'
            && content[i + 1] is (byte)'q' or (byte)'r';
    }

    /// <summary>Foreground, background and cursor colour queries (OSC 10/11/12 with ? or an rgb: reply).</summary>
    private static bool ShouldStripOsc(ReadOnlySpan<byte> content)
    {
        if (content.Length < 4 || content[0] != (byte)'1' || content[1] is not ((byte)'0' or (byte)'1' or (byte)'2') || content[2] != (byte)';')
            return false;
        var value = content[3..];
        return value[0] == (byte)'?' || value.StartsWith("rgb:"u8);
    }

    private static bool AllOf(ReadOnlySpan<byte> body, string allowed)
    {
        foreach (var b in body)
        {
            if (allowed.IndexOf((char)b) < 0)
                return false;
        }
        return true;
    }
}
