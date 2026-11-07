namespace WeaveFleet.Domain.Harnesses;

/// <summary>
/// How much an agent may do without asking, as the user picks it in Settings → Permissions. The same three levels
/// hold for every harness: each adapter turns them into its harness's own rules or mode.
/// </summary>
public static class PermissionLevels
{
    /// <summary>Reading and searching run; anything that changes files, runs a command or goes online asks first.</summary>
    public const string Ask = "ask";

    /// <summary>File edits run as well; commands, web access and anything else ask first.</summary>
    public const string Edits = "edits";

    /// <summary>Nothing asks. What Fleet did before it could ask at all.</summary>
    public const string All = "all";

    /// <summary>Whether <paramref name="value"/> is one of the levels.</summary>
    public static bool IsKnown(string? value) => value is Ask or Edits or All;
}

/// <summary>What happens to asks in a run nobody is watching: an automation's session or a workflow step.</summary>
public static class UnattendedPermissions
{
    /// <summary>Nothing asks, whatever the level: nobody is there to answer.</summary>
    public const string All = "all";

    /// <summary>The same level as a session you watch: the run waits for you at the first ask.</summary>
    public const string Same = "same";

    /// <summary>The same level, but anything that would ask is refused and the agent is told why.</summary>
    public const string Deny = "deny";

    /// <summary>Whether <paramref name="value"/> is one of the choices.</summary>
    public static bool IsKnown(string? value) => value is All or Same or Deny;
}

/// <summary>What an ask is for, whatever the harness calls the tool.</summary>
public static class PermissionKinds
{
    /// <summary>Reading, searching, planning: never asks.</summary>
    public const string Read = "read";

    /// <summary>Writing or changing files.</summary>
    public const string Edit = "edit";

    /// <summary>Running a command.</summary>
    public const string Shell = "shell";

    /// <summary>Fetching or searching the web.</summary>
    public const string Web = "web";

    /// <summary>Anything else: another tool (an MCP server's), a folder outside the session's, a loop the harness caught.</summary>
    public const string Other = "other";

    private static readonly HashSet<string> ReadTools = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenCode and OpenCode 2
        "read", "glob", "grep", "list", "lsp", "codesearch", "todowrite", "todoread", "todo", "skill", "question", "task",
        "subagent", "opencode_list_mcp_resources", "opencode_read_mcp_resource",
        // Claude Code
        "LS", "NotebookRead", "Agent", "ExitPlanMode", "ListMcpResourcesTool", "ReadMcpResourceTool",
    };

    private static readonly HashSet<string> EditTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "edit", "write", "patch", "apply_patch", "multiedit", "move", "NotebookEdit",
    };

    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "shell", "execute", "BashOutput", "KillShell", "KillBash",
    };

    private static readonly HashSet<string> WebTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "webfetch", "websearch",
    };

    /// <summary>
    /// The kind of an ask for <paramref name="tool"/>: the harness's permission or tool name (<c>bash</c>, <c>edit</c>,
    /// <c>Bash</c>, <c>WebFetch</c>). Fleet's own tools (<c>fleet_…</c>) count as reading: they're Fleet's to allow.
    /// </summary>
    public static string Classify(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
            return Other;
        if (ReadTools.Contains(tool) || tool.StartsWith("fleet_", StringComparison.Ordinal))
            return Read;
        if (EditTools.Contains(tool))
            return Edit;
        if (ShellTools.Contains(tool))
            return Shell;
        return WebTools.Contains(tool) ? Web : Other;
    }

    /// <summary>
    /// The harness's names for the tools that never ask, as rules its config or session takes: what an
    /// <see cref="PermissionLevels.Ask"/> ruleset allows after asking for everything else.
    /// </summary>
    public static IReadOnlyList<string> AllowedWithoutAsking { get; } =
    [
        "read", "glob", "grep", "list", "lsp", "codesearch", "todowrite", "todoread", "todo", "skill", "question", "task",
        "subagent", "opencode_list_mcp_resources", "opencode_read_mcp_resource", "fleet_*",
    ];
}

/// <summary>The answers to an ask, as every harness takes them.</summary>
public static class PermissionReplies
{
    /// <summary>Allow this call only.</summary>
    public const string Once = "once";

    /// <summary>Allow it, and anything matching the ask's patterns, for the rest of the session.</summary>
    public const string Always = "always";

    /// <summary>Refuse it; the agent is told, with the user's words when they gave some.</summary>
    public const string Reject = "reject";

    /// <summary>Nobody answered: the harness that asked went away. Only ever in a <c>permission.replied</c> event.</summary>
    public const string Gone = "gone";

    /// <summary>Whether <paramref name="value"/> is an answer a user can give.</summary>
    public static bool IsAnswer(string? value) => value is Once or Always or Reject;
}

/// <summary>
/// What a session may do without asking: its level, and whether an ask is refused rather than put to the user (a run
/// nobody watches, with <see cref="UnattendedPermissions.Deny"/>).
/// </summary>
public sealed record PermissionPolicy(string Level, bool RejectAsks = false, bool ReadOnly = false, string? ReadOnlyRoot = null)
{
    /// <summary>
    /// A run that may look at the repository in <paramref name="directory"/> and change nothing, with nobody to ask
    /// (Dreaming): reading tools, a few reading commands (<see cref="ReadOnlyShell"/>) and <c>fleet_session_read</c> run;
    /// everything else is refused, and so is any path outside <paramref name="directory"/>.
    /// </summary>
    public static PermissionPolicy ReadOnlyIn(string? directory) => new(PermissionLevels.Ask, RejectAsks: true, ReadOnly: true, ReadOnlyRoot: directory);

    /// <summary>Nothing asks: what every harness did before Fleet could ask.</summary>
    public static PermissionPolicy AllowAll { get; } = new(PermissionLevels.All);

    /// <summary>The only Fleet tool a <see cref="ReadOnly"/> run may call.</summary>
    public const string ReadOnlyFleetTool = "fleet_session_read";

    /// <summary>Claude Code's prefix for Fleet's tools (<c>mcp__fleet__fleet_session_read</c>).</summary>
    private const string McpFleetPrefix = "mcp__fleet__";

    /// <summary>
    /// What a <see cref="ReadOnly"/> run does with a call: <see cref="PermissionReplies.Once"/> for a reading tool inside
    /// <see cref="ReadOnlyRoot"/>, <c>fleet_session_read</c> (under either harness's spelling) or a command
    /// <see cref="ReadOnlyShell"/> knows to be harmless; everything else is refused.
    /// </summary>
    public string DecideReadOnly(string tool, IReadOnlyList<string> patterns)
    {
        if (tool.StartsWith(McpFleetPrefix, StringComparison.Ordinal))
            tool = tool[McpFleetPrefix.Length..];
        if (tool.StartsWith("fleet_", StringComparison.Ordinal))
            return string.Equals(tool, ReadOnlyFleetTool, StringComparison.Ordinal) ? PermissionReplies.Once : PermissionReplies.Reject;
        var kind = PermissionKinds.Classify(tool);
        if (kind == PermissionKinds.Read)
            return patterns.All(path => ReadOnlyShell.IsInside(ReadOnlyRoot, path)) ? PermissionReplies.Once : PermissionReplies.Reject;
        if (kind == PermissionKinds.Shell && patterns.Count > 0 && patterns.All(ReadOnlyShell.IsHarmless))
            return PermissionReplies.Once;
        return PermissionReplies.Reject;
    }

    /// <summary>
    /// What happens to an ask of <paramref name="kind"/> (<see cref="PermissionKinds"/>): <see cref="PermissionReplies.Once"/>
    /// when the level allows it, <see cref="PermissionReplies.Reject"/> when asks are refused, otherwise <see langword="null"/>:
    /// the user answers.
    /// </summary>
    public string? Decide(string kind)
    {
        var allowed = Level switch
        {
            PermissionLevels.All => true,
            PermissionLevels.Edits => kind is PermissionKinds.Read or PermissionKinds.Edit,
            _ => kind is PermissionKinds.Read,
        };
        if (allowed)
            return PermissionReplies.Once;
        return RejectAsks ? PermissionReplies.Reject : null;
    }

    /// <summary>What the agent is told when an ask is refused because nobody is there to answer.</summary>
    public const string UnattendedRejection =
        "This run has nobody watching it, and Fleet's permission settings refuse anything that would need asking. "
        + "Do without it, or stop and say what you needed.";
}

/// <summary>
/// An agent's request to do something the session's level doesn't allow, in Fleet's shape: each adapter turns its
/// harness's ask into this and sends it as a <c>permission.asked</c> event. The user answers it with
/// <c>POST /api/sessions/{SessionId}/permissions/{Id}</c>.
/// </summary>
public sealed record PermissionAsk
{
    /// <summary>The harness's id for the ask, which the answer names.</summary>
    public required string Id { get; init; }

    /// <summary>The Fleet session whose harness asked and takes the answer. A subagent's ask can be shown on its parent.</summary>
    public required string SessionId { get; init; }

    /// <summary>What it's for (<see cref="PermissionKinds"/>).</summary>
    public required string Kind { get; init; }

    /// <summary>The harness's name for the tool or permission, e.g. <c>bash</c>, <c>edit</c>, <c>WebFetch</c>.</summary>
    public required string Tool { get; init; }

    /// <summary>What it touches, in one line: the command, the file, the address.</summary>
    public string? Title { get; init; }

    /// <summary>More to look at before answering, when the harness sent it: a diff, a whole script.</summary>
    public string? Detail { get; init; }

    /// <summary>The folder a command runs in, when the harness said.</summary>
    public string? Directory { get; init; }

    /// <summary>What "Don't ask again" would allow, as the harness would match it (<c>git push *</c>); empty when it can't.</summary>
    public IReadOnlyList<string> Always { get; init; } = [];

    /// <summary>The tool call waiting on the answer, when the harness said which.</summary>
    public string? CallId { get; init; }

    /// <summary>The subagent that asked, when it wasn't the session's own agent.</summary>
    public string? Subagent { get; init; }

    /// <summary>When Fleet heard the ask.</summary>
    public DateTimeOffset AskedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Set on an ask Fleet raises itself, not a harness: an agent wants to send work to another machine set to Ask. Once
    /// starts it, Always and Reject save that machine's setting as well (Always or Never).
    /// </summary>
    public HandoffAskDetails? Handoff { get; init; }
}

/// <summary>What an agent wants to send to another machine, as Fleet's ask about it shows it.</summary>
public sealed record HandoffAskDetails
{
    /// <summary>The machine's name.</summary>
    public required string Machine { get; init; }

    public string? Os { get; init; }

    /// <summary>The folder there, for a new session.</summary>
    public string? Folder { get; init; }

    /// <summary>The branch of the new session's fresh worktree; null to work in the folder as it is.</summary>
    public string? Branch { get; init; }

    /// <summary>The harness the new session runs; null for the machine's default.</summary>
    public string? Harness { get; init; }

    /// <summary>The first line of the task, or of the message.</summary>
    public string? Task { get; init; }

    /// <summary>The title of the session there that the agent messages; null for a new session.</summary>
    public string? Session { get; init; }

    /// <summary>The permission level the work runs under there (<see cref="PermissionLevels"/>); null when that machine doesn't say.</summary>
    public string? Level { get; init; }
}

/// <summary>The payload of a <c>permission.replied</c> event: the ask <see cref="Id"/> is answered and no longer waits.</summary>
public sealed record PermissionReplied
{
    /// <summary>The ask's id.</summary>
    public required string Id { get; init; }

    /// <summary>The Fleet session whose harness asked.</summary>
    public required string SessionId { get; init; }

    /// <summary>The answer (<see cref="PermissionReplies"/>).</summary>
    public required string Reply { get; init; }
}

/// <summary>
/// Tells commands that only read from the rest, for a run that may look and not touch. Conservative: one command (or
/// a pipe of them) from a short list, with no redirects, substitutions, <c>&amp;</c> or <c>;</c>, none of the flags that
/// make a reading command write, run something or never end, and no path outside the working directory (a word that
/// starts with <c>/</c> or <c>~</c>, or has <c>..</c> in it).
/// </summary>
public static class ReadOnlyShell
{
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "cat", "head", "tail", "ls", "wc", "grep", "egrep", "fgrep", "rg", "find", "file", "stat", "pwd", "echo",
        "basename", "dirname", "which", "realpath", "readlink", "git",
    };

    private static readonly HashSet<string> GitReading = new(StringComparer.Ordinal)
    {
        "log", "show", "diff", "status", "blame", "ls-files", "ls-tree", "rev-parse", "cat-file", "shortlog", "describe", "grep",
    };

    private static readonly string[] ForbiddenFlags =
    [
        "--output", "--ext-diff", "--textconv", "--pre", "--hostname-bin", "--open-files-in-pager", "--follow", "--retry",
        "-exec", "-execdir", "-ok", "-okdir", "-delete", "-fls",
    ];

    /// <summary>Flags that start with one of these: <c>find -fprint</c>, <c>-fprint0</c>, <c>-fprintf</c>.</summary>
    private static readonly string[] ForbiddenFlagPrefixes = ["-fprint"];

    private static readonly System.Buffers.SearchValues<char> Forbidden = System.Buffers.SearchValues.Create([';', '&', '<', '>', '`', '$', '\n', '\r', '(', ')', '{', '}', '\\']);

    /// <summary>Whether <paramref name="command"/> only reads.</summary>
    public static bool IsHarmless(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.AsSpan().ContainsAny(Forbidden))
            return false;
        return command.Split('|').All(IsHarmlessSegment);
    }

    private static bool IsHarmlessSegment(string segment)
    {
        var words = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !Commands.Contains(words[0]))
            return false;
        var command = words[0];
        if (command == "git" && (words.Length < 2 || !GitReading.Contains(words[1])))
            return false;
        var gitGrep = command == "git" && words[1] == "grep";
        foreach (var word in words.Skip(1))
        {
            if (LeavesTheFolder(word)
                || ForbiddenFlags.Any(flag => word == flag || word.StartsWith(flag + "=", StringComparison.Ordinal))
                || ForbiddenFlagPrefixes.Any(prefix => word.StartsWith(prefix, StringComparison.Ordinal)))
                return false;
            // A short-flag group such as -nOless runs a program (git grep -O) or follows a file forever (tail -f).
            if (IsShortGroup(word)
                && ((gitGrep && word.Contains('O', StringComparison.Ordinal)) || (command == "tail" && word.AsSpan(1).ContainsAny('f', 'F'))))
                return false;
        }

        return true;
    }

    private static bool IsShortGroup(string word) => word.Length > 1 && word[0] == '-' && word[1] != '-';

    /// <summary>A word naming a path outside the working directory: absolute, in the home folder, or going up (also after <c>=</c>, <c>:</c> or <c>,</c>).</summary>
    private static bool LeavesTheFolder(string word)
    {
        if (word.Contains("..", StringComparison.Ordinal))
            return true;
        return word.Split(['=', ':', ','], StringSplitOptions.None)
            .Any(part => part.TrimStart('\'', '"') is ['/', ..] or ['~', ..] or [_, ':', '\\', ..]);
    }

    /// <summary>
    /// Whether <paramref name="path"/> (what a reading tool was asked to touch) is inside <paramref name="root"/>: a relative path
    /// that doesn't go up, or an absolute one under it. With no root nothing absolute is inside.
    /// </summary>
    public static bool IsInside(string? root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "*")
            return true;
        if (path.Contains("..", StringComparison.Ordinal) || path.StartsWith('~'))
            return false;
        if (!Path.IsPathRooted(path))
            return true;
        if (string.IsNullOrWhiteSpace(root))
            return false;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(path);
        var folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (full + Path.DirectorySeparatorChar).StartsWith(folder, comparison);
    }
}
