using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using WeaveFleet.Application.Git;

namespace WeaveFleet.Application.Workspaces;

/// <summary>A span of characters in a line's text, <see cref="Start"/> included and <see cref="End"/> not.</summary>
public sealed record TextRange(int Start, int End);

/// <summary>One matching line. <see cref="Text"/> may be cut around the match; <see cref="Ranges"/> index into it.</summary>
public sealed record TextMatch(int Line, int Column, string Text, IReadOnlyList<TextRange> Ranges);

/// <summary>The matches in one file. <see cref="Total"/> counts every matching line found, kept or not.</summary>
public sealed record TextFileMatches(string Path, int Total, IReadOnlyList<TextMatch> Matches);

/// <param name="Stopped">Null when the search ran to the end; "limit" when it stopped at the cap, "deadline" when it ran out of time.</param>
/// <param name="Engine">"git" or "walk".</param>
public sealed record TextSearchResult(IReadOnlyList<TextFileMatches> Files, int Matches, string? Stopped, string Engine);

/// <summary>The search pattern isn't valid; the message is one sentence fit to show.</summary>
public sealed class InvalidSearchPatternException(string message) : Exception(message);

/// <summary>
/// Finds text inside the files of a folder, for the Files tab's "File contents" search. In a git repository it asks
/// <c>git grep</c>, which skips ignored files and binaries and is fast on big trees; outside one it walks the folder,
/// skipping the usual noise folders, files over 1 MB and binaries. Smart case: a query with no capital letter ignores
/// case unless <c>matchCase</c> is on. Paths use <c>/</c> and are relative to the folder.
/// </summary>
public static class GitGrepSearch
{
    public const int DefaultLimit = 200;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private const long MaxWalkFileBytes = 1_000_000;
    private const int MaxWalkFiles = 50_000;
    private const int MaxRangesPerLine = 8;
    private const int MaxLineChars = 160;
    private const int LeadChars = 16;
    private const int LeadAfter = 28;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "out", "target", "coverage",
        ".venv", "venv", "__pycache__", ".next", ".nuxt", ".turbo", ".cache", ".gradle", ".idea", ".vs",
    };

    /// <exception cref="InvalidSearchPatternException"><paramref name="query"/> is a regex that doesn't parse.</exception>
    public static Task<TextSearchResult> SearchAsync(
        string directory, string query, bool regex, bool matchCase, int limit = DefaultLimit, CancellationToken ct = default) =>
        SearchAsync(directory, query, regex, matchCase, limit, Deadline, ct);

    internal static async Task<TextSearchResult> SearchAsync(
        string directory, string query, bool regex, bool matchCase, int limit, TimeSpan deadline, CancellationToken ct)
    {
        var root = Path.GetFullPath(directory);
        if (query.Length == 0)
            return new TextSearchResult([], 0, null, "git");

        limit = Math.Clamp(limit, 1, 500);
        var ignoreCase = !matchCase && !HasUpperCase(query, regex);

        var viaGit = await TryGitAsync(root, query, regex, ignoreCase, limit, deadline, ct).ConfigureAwait(false);
        return viaGit ?? await Task.Run(() => Walk(root, query, regex, ignoreCase, limit, deadline, ct), ct).ConfigureAwait(false);
    }

    /// <summary>Whether the query has a capital letter; in a regex, escapes such as <c>\S</c> don't count.</summary>
    private static bool HasUpperCase(string query, bool regex)
    {
        for (var i = 0; i < query.Length; i++)
        {
            if (regex && query[i] == '\\')
            {
                i++;
                continue;
            }

            if (char.IsUpper(query[i]))
                return true;
        }

        return false;
    }

    // ── git ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The search through git; null when the folder isn't a git repository or git isn't installed.</summary>
    private static async Task<TextSearchResult?> TryGitAsync(
        string root, string query, bool regex, bool ignoreCase, int limit, TimeSpan deadline, CancellationToken ct)
    {
        var args = new List<string> { "grep", "-n", "-I", "--column", "-z", "--untracked", "--exclude-standard", "--no-color" };
        args.Add(regex ? "-E" : "-F");
        if (ignoreCase)
            args.Add("-i");
        args.Add("-e");
        args.Add(query);

        GitCommandResult result;
        try
        {
            result = await GitCommand.ExecAsync(root, args, deadline, ct).ConfigureAwait(false);
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (GitCommandException)
        {
            return new TextSearchResult([], 0, "deadline", "git");
        }

        if (result.ExitCode == 1)
            return new TextSearchResult([], 0, null, "git");

        if (result.ExitCode != 0)
        {
            if (IsFolderGitCannotUse(result.StandardError))
                return null;

            throw new InvalidSearchPatternException(PatternMessage(result.StandardError, "git extended regex"));
        }

        var matcher = RangeFinder(query, regex, ignoreCase);
        return Group(ParseGit(result.StandardOutput, matcher, query.Length), limit, "git");
    }

    /// <summary>
    /// Whether git's complaint is about the folder, not the pattern: not a repository, or a worktree whose main
    /// repository is gone. Git words that second case by version ("not a git repository: /main/.git/worktrees/x" up to
    /// 2.53, "gitfile does not point to a valid repository" from 2.56), so both are matched.
    /// </summary>
    internal static bool IsFolderGitCannotUse(string stderr) =>
        stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("gitfile does not point to a valid repository", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Git's reason as one sentence: its first line, without "fatal:" or the pattern it quotes, and the flavour named.
    /// </summary>
    private static string PatternMessage(string stderr, string flavour)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "git could not search";
        line = Regex.Replace(line, "^(fatal|error): ", "");
        line = Regex.Replace(line, "^(command-line|-e option), '.*?': ", "");
        line = line.Length > 0 ? char.ToUpperInvariant(line[0]) + line[1..] : line;
        return $"{line.TrimEnd('.')} ({flavour})";
    }

    /// <summary>git grep -z prints <c>path NUL line NUL column NUL text</c> and a newline for each matching line.</summary>
    private static IEnumerable<(string Path, int Line, int Column, string Text, IReadOnlyList<TextRange> Ranges)> ParseGit(
        string output, Func<string, IReadOnlyList<TextRange>> matcher, int queryLength)
    {
        foreach (var record in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var first = record.IndexOf('\0');
            var second = first < 0 ? -1 : record.IndexOf('\0', first + 1);
            var third = second < 0 ? -1 : record.IndexOf('\0', second + 1);
            if (third < 0
                || !int.TryParse(record.AsSpan(first + 1, second - first - 1), out var line)
                || !int.TryParse(record.AsSpan(second + 1, third - second - 1), out var byteColumn))
                continue;

            var text = record[(third + 1)..].TrimEnd('\r');
            var ranges = matcher(text);
            if (ranges.Count == 0)
            {
                // The .NET regex disagrees with git's; mark where git says the match starts.
                var start = CharIndexOfByte(text, byteColumn - 1);
                ranges = [new TextRange(start, Math.Min(text.Length, start + Math.Max(1, queryLength)))];
            }

            yield return (record[..first].Replace('\\', '/'), line, ranges[0].Start + 1, text, ranges);
        }
    }

    private static int CharIndexOfByte(string text, int byteOffset)
    {
        var bytes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (bytes >= byteOffset)
                return i;
            bytes += Encoding.UTF8.GetByteCount(text.AsSpan(i, char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1));
            if (char.IsHighSurrogate(text[i]))
                i++;
        }

        return text.Length;
    }

    // ── matching, shared ──────────────────────────────────────────────────────────────────────────────

    private static Func<string, IReadOnlyList<TextRange>> RangeFinder(string query, bool regex, bool ignoreCase)
    {
        Regex re;
        try
        {
            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            re = new Regex(regex ? query : Regex.Escape(query), options, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return _ => [];
        }

        return text =>
        {
            var ranges = new List<TextRange>();
            try
            {
                for (var m = re.Match(text); m.Success && ranges.Count < MaxRangesPerLine; m = m.NextMatch())
                {
                    if (m.Length > 0)
                        ranges.Add(new TextRange(m.Index, m.Index + m.Length));
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }

            return ranges;
        };
    }

    /// <summary>Groups lines by file in the order they came, keeping the first <paramref name="limit"/> and cutting long lines.</summary>
    private static TextSearchResult Group(
        IEnumerable<(string Path, int Line, int Column, string Text, IReadOnlyList<TextRange> Ranges)> found, int limit, string engine)
    {
        var files = new List<(string Path, int Total, List<TextMatch> Kept)>();
        var kept = 0;
        var capped = false;

        foreach (var hit in found)
        {
            if (files.Count == 0 || files[^1].Path != hit.Path)
                files.Add((hit.Path, 0, []));

            var file = files[^1];
            files[^1] = file with { Total = file.Total + 1 };
            if (kept >= limit)
            {
                capped = true;
                continue;
            }

            file.Kept.Add(Cut(hit.Line, hit.Column, hit.Text, hit.Ranges));
            kept++;
        }

        var shown = files.Where(f => f.Kept.Count > 0)
            .Select(f => new TextFileMatches(f.Path, f.Total, f.Kept))
            .ToList();
        return new TextSearchResult(shown, kept, capped ? "limit" : null, engine);
    }

    /// <summary>Drops the indent and cuts a long line to a window around the first match, with … where it was cut.</summary>
    private static TextMatch Cut(int line, int column, string text, IReadOnlyList<TextRange> ranges)
    {
        var indent = text.Length - text.TrimStart().Length;
        var start = indent;
        if (ranges.Count > 0 && ranges[0].Start - indent > LeadAfter)
            start = ranges[0].Start - LeadChars;

        var length = Math.Min(MaxLineChars, text.Length - start);
        var cutStart = start > indent;
        var cutEnd = start + length < text.Length;
        var shown = (cutStart ? "…" : "") + text.Substring(start, length).TrimEnd() + (cutEnd ? "…" : "");
        var shift = (cutStart ? 1 : 0) - start;

        var moved = ranges
            .Select(r => new TextRange(r.Start + shift, Math.Min(r.End + shift, shown.Length - (cutEnd ? 1 : 0))))
            .Where(r => r.Start >= (cutStart ? 1 : 0) && r.Start < r.End)
            .ToList();
        return new TextMatch(line, column, shown, moved);
    }

    // ── no git: walk the folder ───────────────────────────────────────────────────────────────────────

    private static TextSearchResult Walk(string root, string query, bool regex, bool ignoreCase, int limit, TimeSpan deadline, CancellationToken ct)
    {
        Regex re;
        try
        {
            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            re = new Regex(regex ? query : Regex.Escape(query), options, RegexTimeout);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidSearchPatternException($"{exception.Message.TrimEnd('.')} (.NET regex)");
        }

        var clock = Stopwatch.StartNew();
        var found = new List<(string Path, int Line, int Column, string Text, IReadOnlyList<TextRange> Ranges)>();
        var cappedAt = limit;
        string? stopped = null;

        foreach (var path in ListFiles(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (clock.Elapsed > deadline)
            {
                stopped = "deadline";
                break;
            }

            if (found.Count > cappedAt * 4)
                break;

            ReadMatches(root, path, re, found);
        }

        var grouped = Group(found, limit, "walk");
        return stopped is null || grouped.Stopped is not null ? grouped : grouped with { Stopped = stopped };
    }

    private static void ReadMatches(
        string root, string path, Regex re,
        List<(string Path, int Line, int Column, string Text, IReadOnlyList<TextRange> Ranges)> found)
    {
        try
        {
            var full = Path.Combine(root, path);
            var info = new FileInfo(full);
            if (info.Length > MaxWalkFileBytes || IsBinary(full))
                return;

            var number = 0;
            foreach (var text in File.ReadLines(full))
            {
                number++;
                var ranges = new List<TextRange>();
                for (var m = re.Match(text); m.Success && ranges.Count < MaxRangesPerLine; m = m.NextMatch())
                {
                    if (m.Length > 0)
                        ranges.Add(new TextRange(m.Index, m.Index + m.Length));
                }

                if (ranges.Count > 0)
                    found.Add((path, number, ranges[0].Start + 1, text.TrimEnd('\r'), ranges));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RegexMatchTimeoutException or DecoderFallbackException)
        {
            // An unreadable file, or a line the pattern chokes on, is left out.
        }
    }

    private static bool IsBinary(string full)
    {
        using var stream = File.OpenRead(full);
        var buffer = new byte[8000];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }

    private static IEnumerable<string> ListFiles(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var walk = new System.IO.Enumeration.FileSystemEnumerable<string>(
            root,
            (ref System.IO.Enumeration.FileSystemEntry entry) => Path.GetRelativePath(root, entry.ToFullPath()).Replace('\\', '/'),
            options)
        {
            ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
                !entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0,
            ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
                !SkippedFolders.Contains(entry.FileName.ToString()) && (entry.Attributes & FileAttributes.ReparsePoint) == 0,
        };

        return walk.Take(MaxWalkFiles);
    }
}
