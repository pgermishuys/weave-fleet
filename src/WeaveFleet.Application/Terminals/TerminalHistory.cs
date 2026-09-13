namespace WeaveFleet.Application.Terminals;

/// <summary>
/// A terminal's saved output: the most recent lines, capped by line count and by size. When it's over
/// either cap it drops the oldest output, cutting just after a line break so a replay starts on a clean
/// line. Not thread-safe.
/// </summary>
public sealed class TerminalHistory
{
    private const int ChunkSize = 16 * 1024;

    private readonly int _maxLines;
    private readonly int _maxBytes;
    private readonly LinkedList<byte[]> _chunks = new();

    public TerminalHistory(int maxLines, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _maxLines = maxLines;
        _maxBytes = maxBytes;
    }

    /// <summary>Total bytes held.</summary>
    public int Length { get; private set; }

    /// <summary>Line breaks held.</summary>
    public int LineBreaks { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var take = Math.Min(data.Length, ChunkSize);
            var chunk = data[..take].ToArray();
            _chunks.AddLast(chunk);
            Length += chunk.Length;
            LineBreaks += CountLineBreaks(chunk);
            data = data[take..];
        }

        Trim();
    }

    /// <summary>Replaces everything with <paramref name="saved"/>, applying the caps.</summary>
    public void Load(ReadOnlySpan<byte> saved)
    {
        Clear();
        Append(saved);
    }

    public void Clear()
    {
        _chunks.Clear();
        Length = 0;
        LineBreaks = 0;
    }

    public byte[] ToArray()
    {
        var result = new byte[Length];
        var offset = 0;
        foreach (var chunk in _chunks)
        {
            chunk.CopyTo(result, offset);
            offset += chunk.Length;
        }
        return result;
    }

    private void Trim()
    {
        while (LineBreaks > _maxLines || Length > _maxBytes)
        {
            var first = _chunks.First!.Value;
            var linesToDrop = Math.Max(0, LineBreaks - _maxLines);
            var bytesToDrop = Math.Max(0, Length - _maxBytes);

            var cut = FindCut(first, linesToDrop, bytesToDrop);
            if (cut < 0)
            {
                if (_chunks.Count > 1)
                {
                    DropFirstChunk();
                    continue;
                }

                // One chunk with no line break to cut at: keep its tail, starting on a whole character.
                cut = Utf8Start(first, Math.Min(bytesToDrop, first.Length));
            }

            if (cut >= first.Length)
            {
                DropFirstChunk();
                continue;
            }

            var kept = first[cut..];
            LineBreaks -= CountLineBreaks(first.AsSpan(0, cut));
            Length -= cut;
            _chunks.First.Value = kept;
        }
    }

    /// <summary>
    /// The first position just after a line break by which at least <paramref name="lines"/> line breaks and
    /// <paramref name="bytes"/> bytes have gone, or -1 when this chunk isn't enough.
    /// </summary>
    private static int FindCut(byte[] chunk, int lines, int bytes)
    {
        var seen = 0;
        for (var i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] != (byte)'\n')
                continue;
            seen++;
            if (seen >= lines && i + 1 >= bytes)
                return i + 1;
        }
        return -1;
    }

    private void DropFirstChunk()
    {
        var first = _chunks.First!.Value;
        Length -= first.Length;
        LineBreaks -= CountLineBreaks(first);
        _chunks.RemoveFirst();
    }

    /// <summary>Moves <paramref name="index"/> forward past UTF-8 continuation bytes.</summary>
    private static int Utf8Start(byte[] chunk, int index)
    {
        while (index < chunk.Length && (chunk[index] & 0xC0) == 0x80)
            index++;
        return index;
    }

    private static int CountLineBreaks(ReadOnlySpan<byte> data) => data.Count((byte)'\n');
}
