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
public sealed record PermissionPolicy(string Level, bool RejectAsks = false)
{
    /// <summary>Nothing asks: what every harness did before Fleet could ask.</summary>
    public static PermissionPolicy AllowAll { get; } = new(PermissionLevels.All);

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
