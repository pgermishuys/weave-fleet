using System.Globalization;
using System.Text;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Application.Reports;

/// <summary>
/// The part of Fleet's own log a problem report carries: the last minutes of the day's files (see
/// <c>FileLoggerProvider</c>), keeping the lines about the reported session and every warning and error.
/// Nothing is recorded for reports; this only reads what Fleet already writes.
/// </summary>
public static class FleetLogExcerpt
{
    /// <summary>How much of the end of each file is read, at most.</summary>
    public const int MaxBytesPerFile = 2 * 1024 * 1024;

    /// <summary>The excerpt stops growing here; the newest entries are kept.</summary>
    public const int MaxChars = 200_000;

    /// <summary>At most this many entries: Fleet's Debug log names a busy session on nearly every line.</summary>
    public const int MaxEntries = 400;

    /// <summary>One log entry: its first line and any lines that follow it, such as an exception's stack.</summary>
    public sealed record Entry(DateTime TimeUtc, string Level, string Text);

    public sealed record Excerpt(string Text, int Included, int LeftOut);

    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>Reads the entries written from <paramref name="fromUtc"/> on, from the log files that cover that time.</summary>
    public static async Task<IReadOnlyList<Entry>> ReadAsync(
        FleetLogLocation location, DateTime fromUtc, DateTime nowUtc, CancellationToken ct)
    {
        var entries = new List<Entry>();
        for (var day = DateOnly.FromDateTime(fromUtc); day <= DateOnly.FromDateTime(nowUtc); day = day.AddDays(1))
        {
            var path = Path.Combine(location.Directory, $"{location.FilePrefix}-{day:yyyy-MM-dd}.log");
            if (!File.Exists(path)) continue;
            var text = await ReadTailAsync(path, ct).ConfigureAwait(false);
            entries.AddRange(Parse(text).Where(entry => entry.TimeUtc >= fromUtc));
        }

        return entries;
    }

    /// <summary>
    /// Keeps warnings and errors, and entries that mention any of <paramref name="ids"/> (the session's ids).
    /// </summary>
    public static Excerpt Select(IReadOnlyList<Entry> entries, IReadOnlyCollection<string> ids)
    {
        var kept = entries
            .Where(entry => entry.Level is "WRN" or "ERR" or "CRT"
                || ids.Any(id => id.Length > 0 && entry.Text.Contains(id, StringComparison.Ordinal)))
            .ToList();

        // Newest last; drop the oldest until it fits.
        var length = kept.Sum(entry => entry.Text.Length + 1);
        var start = Math.Max(0, kept.Count - MaxEntries);
        for (var i = 0; i < start; i++) length -= kept[i].Text.Length + 1;
        while (length > MaxChars && start < kept.Count)
        {
            length -= kept[start].Text.Length + 1;
            start++;
        }

        var text = new StringBuilder();
        for (var i = start; i < kept.Count; i++)
            text.Append(kept[i].Text).Append('\n');

        var included = kept.Count - start;
        return new Excerpt(text.ToString(), included, entries.Count - included);
    }

    public static IReadOnlyList<Entry> Parse(string text)
    {
        var entries = new List<Entry>();
        DateTime? time = null;
        string? level = null;
        var current = new StringBuilder();

        void Flush()
        {
            if (time is { } t && level is not null)
                entries.Add(new Entry(t, level, current.ToString().TrimEnd('\r', '\n')));
            current.Clear();
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (TryReadStart(line, out var lineTime, out var lineLevel))
            {
                Flush();
                time = lineTime;
                level = lineLevel;
                current.Append(line);
            }
            else if (time is not null && line.Length > 0)
            {
                // A continuation, such as a stack trace line.
                current.Append('\n').Append(line);
            }
        }

        Flush();
        return entries;
    }

    private static bool TryReadStart(string line, out DateTime time, out string level)
    {
        time = default;
        level = string.Empty;
        // "2026-09-27 14:02:03.911 [WRN] [Category] message"
        if (line.Length < TimestampFormat.Length + 6 || line[TimestampFormat.Length] != ' ' || line[TimestampFormat.Length + 1] != '[')
            return false;
        if (!DateTime.TryParseExact(line.AsSpan(0, TimestampFormat.Length), TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out time))
            return false;
        level = line.Substring(TimestampFormat.Length + 2, 3);
        return true;
    }

    private static async Task<string> ReadTailAsync(string path, CancellationToken ct)
    {
        // Fleet's logger holds the file open for writing and lets others read it.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var skipped = stream.Length > MaxBytesPerFile;
        if (skipped) stream.Seek(-MaxBytesPerFile, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (!skipped) return text;
        // Started mid-line: drop the partial first line.
        var newline = text.IndexOf('\n');
        return newline < 0 ? string.Empty : text[(newline + 1)..];
    }
}
