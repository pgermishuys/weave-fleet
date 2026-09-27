using System.Text;
using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Reports;

/// <summary>
/// Replaces private details in a problem report with labels like <c>‹folder-1›</c> before anyone sees it. Fleet knows
/// most of these values already (folders, session titles, branches, machines, its own tokens), so they're replaced
/// exactly; patterns only catch what Fleet can't know (other tokens, email addresses, private network addresses).
/// <para>
/// One redactor serves one report: the same value gets the same label in every part of it, so <c>‹folder-1›</c> in the
/// description and in the log is the same folder.
/// </para>
/// </summary>
public sealed class ReportRedactor
{
    /// <summary>What a label stands for, as the review screen shows it. Secrets are shown cut short.</summary>
    public sealed record Replacement(string Label, string Kind, string Shown);

    private sealed record Known(string Label, string Kind, bool WholeWord);

    // Folder names too generic to stand for a private repository.
    private static readonly HashSet<string> GenericFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "app", "apps", "source", "sources", "code", "work", "dev", "repo", "repos", "git", "projects",
        "project", "home", "users", "user", "tmp", "temp", "test", "tests", "docs", "main", "documents", "desktop",
        "downloads", "client", "server", "web", "api", "lib", "workspace", "workspaces", "worktrees", ".claude",
    };

    private static readonly HashSet<string> GenericBranches = new(StringComparer.OrdinalIgnoreCase)
    {
        "main", "master", "develop", "development", "dev", "trunk", "head", "release",
    };

    private static readonly HashSet<string> GenericUserNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "users", "admin", "root", "dev", "test", "runner", "ubuntu", "app", "fleet", "local-user", "default",
    };

    private static readonly HashSet<string> GenericTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "untitled", "new session",
    };

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    // Tokens Fleet can't know by value. Prefixes like "Bearer " or "password=" stay, so the log still reads.
    private const string SecretPattern =
        @"\bgh[pousr]_[A-Za-z0-9]{20,}\b"
        + @"|\bgithub_pat_[A-Za-z0-9_]{20,}\b"
        + @"|\bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{20,}\b"
        + @"|\bxox[abpr]-[A-Za-z0-9-]{10,}\b"
        + @"|\bAKIA[0-9A-Z]{16}\b"
        + @"|\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b"
        + @"|(?<=\bBearer\s+)[A-Za-z0-9._~+/=-]{16,}"
        + @"|(?<=\b(?:password|passwd|secret|api[_-]?key|access[_-]?token|token)\s*[=:]\s*)(?=[^\s""'&,;]*[A-Za-z])[^\s""'&,;]{6,}";

    private const string EmailPattern = @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b";

    // Private network addresses: 10/8, 172.16/12, 192.168/16 and 100.64/10 (Tailscale and other carrier-grade NAT).
    private const string AddressPattern =
        @"\b(?:10\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])|192\.168|100\.(?:6[4-9]|[7-9]\d|1[01]\d|12[0-7]))\.\d{1,3}\.\d{1,3}\b";

    private readonly Dictionary<string, Known> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _shownByLabel = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _kindByLabel = new(StringComparer.Ordinal);
    private readonly List<string> _usedLabels = [];
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private Regex? _regex;
    private string? _home;

    /// <summary>A folder, with its path in the forms logs write it and its name.</summary>
    public void AddFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var trimmed = path.Trim().TrimEnd('/', '\\');
        if (trimmed.Length < 2 || trimmed is "~") return;

        var label = LabelFor("folder", trimmed);
        foreach (var variant in PathVariants(trimmed))
            AddValue(variant, label, "folder", wholeWord: false);

        var name = LastSegment(trimmed);
        if (name.Length >= 3 && !GenericFolderNames.Contains(name))
            AddValue(name, label, "folder", wholeWord: true);
    }

    public void AddSessionTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var trimmed = title.Trim();
        if (trimmed.Length < 4 || GenericTitles.Contains(trimmed)) return;
        AddValue(trimmed, LabelFor("session", trimmed), "session", wholeWord: true);
    }

    public void AddBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return;
        var trimmed = branch.Trim();
        if (trimmed.Length < 3 || GenericBranches.Contains(trimmed)) return;
        AddValue(trimmed, LabelFor("branch", trimmed), "branch", wholeWord: true);
    }

    /// <summary>
    /// The account Fleet runs as: its home folder anywhere, and its name as a whole word. Add it before folders, so
    /// folders under the home folder are also found written as <c>~/…</c>.
    /// </summary>
    public void AddUser(string? userName, string? homeDirectory)
    {
        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            var home = homeDirectory.Trim().TrimEnd('/', '\\');
            if (home.Length > 1)
            {
                _home = home;
                foreach (var variant in PathVariants(home))
                    AddValue(variant, "‹home›", "user", wholeWord: false);
                _shownByLabel.TryAdd("‹home›", home);
                _kindByLabel.TryAdd("‹home›", "user");
            }
        }

        if (!string.IsNullOrWhiteSpace(userName))
        {
            var name = userName.Trim();
            if (name.Length >= 3 && !GenericUserNames.Contains(name))
            {
                AddValue(name, "‹user›", "user", wholeWord: true);
                _shownByLabel.TryAdd("‹user›", name);
                _kindByLabel.TryAdd("‹user›", "user");
            }
        }
    }

    public void AddMachine(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        if (trimmed.Length < 3 || trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return;
        AddValue(trimmed, LabelFor("machine", trimmed), "machine", wholeWord: true);
    }

    /// <summary>An address Fleet or the browser uses, such as another machine's URL; its host gets the same label.</summary>
    public void AddUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var trimmed = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.IsLoopback) return;

        var label = LabelFor("url", trimmed);
        AddValue(trimmed, label, "url", wholeWord: false);
        if (uri.Host.Length >= 3 && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            AddValue(uri.Host, label, "url", wholeWord: true);
    }

    /// <summary>A secret Fleet holds (its access token, a saved credential). Short values are ignored.</summary>
    public void AddSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        if (trimmed.Length < 8) return;
        AddValue(trimmed, LabelFor("secret", trimmed), "secret", wholeWord: false);
    }

    /// <summary>Replaces every private detail in <paramref name="text"/>.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        _regex ??= BuildRegex();
        return _regex.Replace(text, Replace);
    }

    /// <summary>The labels that appeared in redacted text so far, in the order they first appeared.</summary>
    public IReadOnlyList<Replacement> Replacements =>
        _usedLabels.Select(label => new Replacement(label, _kindByLabel[label], _shownByLabel[label])).ToList();

    /// <summary>How many different values of each kind were replaced so far.</summary>
    public IReadOnlyDictionary<string, int> Counts =>
        _usedLabels.GroupBy(label => _kindByLabel[label]).ToDictionary(group => group.Key, group => group.Count());

    private string Replace(Match match)
    {
        if (match.Groups["label"].Success) return match.Value;

        string label;
        string kind;
        if (match.Groups["secret"].Success)
        {
            (label, kind) = (LabelForFound("secret", match.Value), "secret");
        }
        else if (match.Groups["email"].Success)
        {
            (label, kind) = (LabelForFound("email", match.Value), "email");
        }
        else if (match.Groups["address"].Success)
        {
            (label, kind) = (LabelForFound("address", match.Value), "machine");
        }
        else if (_known.TryGetValue(match.Value, out var known))
        {
            (label, kind) = (known.Label, known.Kind);
        }
        else
        {
            return match.Value;
        }

        if (_used.Add(label))
        {
            _usedLabels.Add(label);
            _kindByLabel.TryAdd(label, kind);
        }

        return label;
    }

    // Values found by pattern get labels of their own, one per distinct value.
    private string LabelForFound(string kind, string value)
    {
        if (_known.TryGetValue(value, out var known)) return known.Label;
        var label = LabelFor(kind, value);
        _known[value] = new Known(label, kind is "address" ? "machine" : kind, WholeWord: false);
        _kindByLabel.TryAdd(label, kind is "address" ? "machine" : kind);
        return label;
    }

    private string LabelFor(string kind, string value)
    {
        if (_known.TryGetValue(value, out var existing)) return existing.Label;
        var next = _counters.GetValueOrDefault(kind) + 1;
        _counters[kind] = next;
        var label = $"‹{kind}-{next}›";
        _shownByLabel[label] = kind is "secret" ? Mask(value) : value;
        _kindByLabel[label] = kind is "address" ? "machine" : kind;
        return label;
    }

    private void AddValue(string value, string label, string kind, bool wholeWord)
    {
        if (value.Length == 0) return;
        // A value added twice keeps its first label; a whole-word entry never downgrades a substring one.
        if (_known.TryGetValue(value, out var existing) && (!existing.WholeWord || wholeWord)) return;
        _known[value] = new Known(label, kind, wholeWord);
        _regex = null;
    }

    private Regex BuildRegex()
    {
        var pattern = new StringBuilder();
        // Labels already in the text are matched first and left as they are, so nothing inside one is replaced again.
        pattern.Append(@"(?<label>‹[a-z]+(?:-\d+)?›)|");
        pattern.Append("(?<known>");
        var first = true;
        // Longest first, so a folder's full path wins over its parent or its name at the same place.
        foreach (var (value, known) in _known.OrderByDescending(pair => pair.Key.Length))
        {
            if (!first) pattern.Append('|');
            first = false;
            var escaped = Regex.Escape(value);
            pattern.Append(known.WholeWord ? $@"(?<![\p{{L}}\p{{N}}_]){escaped}(?![\p{{L}}\p{{N}}_])" : escaped);
        }

        if (first) pattern.Append("(?!)");
        pattern.Append(')');
        pattern.Append($"|(?<secret>{SecretPattern})");
        pattern.Append($"|(?<email>{EmailPattern})");
        pattern.Append($"|(?<address>{AddressPattern})");
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    }

    private IEnumerable<string> PathVariants(string path)
    {
        yield return path;
        // People write paths under their home folder as ~/…, and so do some tools.
        if (_home is { } home && path.Length > home.Length + 1
            && path.StartsWith(home, StringComparison.OrdinalIgnoreCase) && path[home.Length] is '/' or '\\')
        {
            yield return "~" + path[home.Length..];
            if (path.Contains('\\')) yield return "~" + path[home.Length..].Replace('\\', '/');
        }

        if (path.Contains('\\'))
        {
            yield return path.Replace('\\', '/');
            // JSON and C# strings double the backslashes.
            yield return path.Replace("\\", "\\\\");
        }
    }

    private static string LastSegment(string path)
    {
        var index = path.LastIndexOfAny(['/', '\\']);
        return index < 0 ? path : path[(index + 1)..];
    }

    private static string Mask(string value) => value.Length <= 6 ? "…" : $"{value[..4]}…";
}
