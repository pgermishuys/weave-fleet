using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Domain.Harnesses;

/// <summary>Lifecycle status of a running harness session.</summary>
public enum HarnessSessionStatus
{
    Starting,
    Running,
    Idle,
    Stopping,
    Stopped,
    Error
}

/// <summary>Declares what a harness supports so the frontend can adapt its UI.</summary>
public sealed record HarnessCapabilities
{
    public bool RequiresInitialPrompt { get; init; }
    public bool SupportsAgents { get; init; }
    public bool SupportsModelSelection { get; init; }
    public bool SupportsCommands { get; init; }
    public bool SupportsForking { get; init; }
    public bool SupportsResume { get; init; }
    public bool SupportsImageAttachments { get; init; }
    public bool SupportsStreaming { get; init; }
    public bool SupportsDelegation { get; init; }

    /// <summary>The harness sends the agent's todo list as <see cref="EventTypes.TodosReported"/> events.</summary>
    public bool ReportsTodos { get; init; }

    /// <summary>The harness sends the files the agent writes as <see cref="EventTypes.FilesWritten"/> events.</summary>
    public bool ReportsFileWrites { get; init; }

    /// <summary>Whether <see cref="IHarnessSession.AskOffTheRecordAsync"/> can answer (used for session recaps).</summary>
    public bool SupportsOffTheRecordPrompt { get; init; }

    /// <summary>A session can start with a profile: harness config the user keeps in Fleet and picks per session.</summary>
    public bool SupportsProfiles { get; init; }

    /// <summary>
    /// The harness keeps the conversation itself, and <see cref="IHarnessSession.GetMessagesAsync"/> reads it. A
    /// reopened session shows the harness's history, and Fleet's own copy (the prompts it saved) is only a partial
    /// fallback while the harness can't be reached. Otherwise Fleet's stored messages are the history.
    /// </summary>
    public bool HistoryLivesInHarness { get; init; }
}

/// <summary>What a harness needs before sessions can use it. Sent to the client as <c>state</c>.</summary>
public static class HarnessStates
{
    /// <summary>Installed and working; sessions can use it.</summary>
    public const string Ready = "ready";

    /// <summary>Fleet can't find its executable.</summary>
    public const string NotInstalled = "not-installed";

    /// <summary>Installed, but the user has to sign in first.</summary>
    public const string SignInRequired = "sign-in-required";

    /// <summary>Found, but it fails when Fleet runs it.</summary>
    public const string NotWorking = "not-working";

    /// <summary>Installed, but older than the version Fleet needs.</summary>
    public const string UpdateNeeded = "update-needed";
}

/// <summary>Whether a harness binary/service is available on this machine.</summary>
/// <param name="Available">Sessions can use the harness.</param>
/// <param name="Reason">Why not, in a sentence the user can act on; <see langword="null"/> when available.</param>
public sealed record HarnessAvailability(bool Available, string? Reason)
{
    /// <summary>One of <see cref="HarnessStates"/>.</summary>
    public string State { get; init; } = Available ? HarnessStates.Ready : HarnessStates.NotWorking;

    /// <summary>The version the executable reports, when Fleet could run it.</summary>
    public string? Version { get; init; }

    /// <summary>Where Fleet found the executable.</summary>
    public string? ExecutablePath { get; init; }

    public static HarnessAvailability Ready(string? version, string? executablePath) =>
        new(true, null) { Version = version, ExecutablePath = executablePath };

    public static HarnessAvailability NotInstalled(string reason) =>
        new(false, reason) { State = HarnessStates.NotInstalled };

    public static HarnessAvailability SignInRequired(string reason, string? version, string? executablePath) =>
        new(false, reason) { State = HarnessStates.SignInRequired, Version = version, ExecutablePath = executablePath };

    public static HarnessAvailability NotWorking(string reason, string? version = null, string? executablePath = null) =>
        new(false, reason) { State = HarnessStates.NotWorking, Version = version, ExecutablePath = executablePath };

    public static HarnessAvailability UpdateNeeded(string reason, string? version, string? executablePath) =>
        new(false, reason) { State = HarnessStates.UpdateNeeded, Version = version, ExecutablePath = executablePath };
}

/// <summary>A command Fleet runs itself, such as a harness's updater.</summary>
/// <param name="Executable">The program to start.</param>
/// <param name="Arguments">Its arguments, passed without a shell.</param>
/// <param name="Display">The same command as the user would type it, to show or copy.</param>
public sealed record HarnessCommand(string Executable, IReadOnlyList<string> Arguments, string Display);

/// <summary>Compares harness versions such as "1.18.30" or "2.1.276"; a pre-release suffix is ignored.</summary>
public static class HarnessVersion
{
    /// <summary>Negative when <paramref name="a"/> is older than <paramref name="b"/>, zero when equal, positive when newer.</summary>
    public static int Compare(string a, string b)
    {
        var left = Parts(a);
        var right = Parts(b);
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var difference = (i < left.Length ? left[i] : 0).CompareTo(i < right.Length ? right[i] : 0);
            if (difference != 0) return difference;
        }
        return 0;
    }

    /// <summary>True when <paramref name="version"/> is older than <paramref name="other"/>.</summary>
    public static bool IsOlder(string version, string other) => Compare(version, other) < 0;

    private static int[] Parts(string version) =>
        [.. version.TrimStart('v', 'V').Split('-', '+')[0]
            .Split('.')
            .Select(part => int.TryParse(part, out var number) ? number : 0)];
}

/// <summary>
/// How to install a harness or sign in to it on the machine Fleet runs on. Fleet types a command into a
/// terminal and the user presses Enter to run it; Fleet never runs these on its own.
/// </summary>
/// <param name="InstallCommand">The vendor's installer for this platform, or <see langword="null"/> when there isn't one to offer.</param>
/// <param name="SignInCommand">Signs in to the harness, when it needs a sign-in.</param>
/// <param name="DocsUrl">The harness's install instructions, for anything the commands don't cover.</param>
public sealed record HarnessSetup(string? InstallCommand, string? SignInCommand, string? DocsUrl)
{
    /// <summary>
    /// Where to download the harness by hand, when there's no installer to type for this platform
    /// (<see cref="InstallCommand"/> is <see langword="null"/>).
    /// </summary>
    public string? DownloadUrl { get; init; }

    /// <summary>How the harness is (or will be) installed here, in a few words, when it has more than one way.</summary>
    public string? Mode { get; init; }

    /// <summary>The folders this install uses: the program, its settings, its data.</summary>
    public IReadOnlyList<HarnessFolder>? Folders { get; init; }

    /// <summary>What the user should know about this install, one sentence each; shown with the install command and in Settings.</summary>
    public IReadOnlyList<string>? Notes { get; init; }
}

/// <summary>A folder a harness install uses.</summary>
/// <param name="Label">What it holds, e.g. "Program" or "Sessions".</param>
/// <param name="Path">Where it is.</param>
public sealed record HarnessFolder(string Label, string Path);

/// <summary>A real-time event emitted by a harness instance.</summary>
public sealed record HarnessEvent
{
    public required string Type { get; init; }
    public required string SessionId { get; init; }
    public string? FleetSessionId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public JsonElement? Payload { get; init; }
}

/// <summary>Discriminator for <see cref="MessagePart"/> subtypes.</summary>
public enum MessagePartKind
{
    Text = 0,
    ToolUse = 1,
    ToolResult = 2,
    Reasoning = 3,
    File = 4,
    StepFinish = 5,
    Agent = 6,
    Subtask = 7,
    Patch = 8,
}

/// <summary>One logical piece of an agent message.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ToolUsePart), "tool")]
[JsonDerivedType(typeof(ToolResultPart), "tool-result")]
[JsonDerivedType(typeof(ReasoningPart), "reasoning")]
[JsonDerivedType(typeof(FilePart), "file")]
[JsonDerivedType(typeof(StepFinishPart), "step-finish")]
[JsonDerivedType(typeof(AgentPart), "agent")]
[JsonDerivedType(typeof(SubtaskPart), "subtask")]
[JsonDerivedType(typeof(PatchPart), "patch")]
public abstract record MessagePart(MessagePartKind Kind);

/// <summary>Plain text content.</summary>
public sealed record TextPart(string Text) : MessagePart(MessagePartKind.Text)
{
    /// <summary>The harness's own id for this part, so history and live updates name the same part.</summary>
    public string? PartId { get; init; }
}

/// <summary>Structured reasoning content that is stored but not shown in the main activity stream.</summary>
public sealed record ReasoningPart(string Text, string? Summary = null) : MessagePart(MessagePartKind.Reasoning)
{
    /// <summary>The harness's own id for this part, so history and live updates name the same part.</summary>
    public string? PartId { get; init; }
}

/// <summary>A file or image attachment associated with a message.</summary>
public sealed record FilePart(string PartId, string Mime, string Url, string? Filename) : MessagePart(MessagePartKind.File);

/// <summary>Marks a completed inference step with final token/cost totals.</summary>
public sealed record StepFinishPart(
    int Index,
    string? Reason,
    double Cost,
    double TokensInput,
    double TokensOutput,
    double TokensReasoning,
    long? CompletedAt) : MessagePart(MessagePartKind.StepFinish);

/// <summary>The agent invoking a tool.</summary>
public sealed record ToolUsePart(
    string ToolCallId,
    string ToolName,
    JsonElement Arguments,
    ToolUseState State) : MessagePart(MessagePartKind.ToolUse)
{
    /// <summary>The harness's own id for this part, so history and live updates name the same part.</summary>
    public string? PartId { get; init; }

    /// <summary>What the tool returned, for harnesses that keep the result on the call itself.</summary>
    public JsonElement? Output { get; init; }

    /// <summary>The error text when the tool failed.</summary>
    public string? Error { get; init; }

    /// <summary>The harness's short heading for the call.</summary>
    public string? Title { get; init; }

    /// <summary>Extra facts the tool reported, such as the child session a sub-agent ran in.</summary>
    public JsonElement? Metadata { get; init; }

    /// <summary>
    /// Whether the call has returned and only its work carries on, out of the turn (a <see cref="ToolUseState.Running"/>
    /// call OpenCode 2 moved into the background).
    /// </summary>
    public bool Background { get; init; }
}

/// <summary>Output returned by a tool invocation.</summary>
public sealed record ToolResultPart(
    string ToolCallId,
    string Content,
    bool IsError) : MessagePart(MessagePartKind.ToolResult);

/// <summary>Lifecycle state of a tool invocation.</summary>
public enum ToolUseState { Pending, Running, Completed, Error }

/// <summary>Sub-agent delegation part indicating which agent handled a delegated task.</summary>
public sealed record AgentPart(
    string? Agent,
    string? Input,
    string? Output) : MessagePart(MessagePartKind.Agent);

/// <summary>Subtask part linking parent and child session activity.</summary>
public sealed record SubtaskPart(
    string? Prompt,
    string? Description,
    string? Agent,
    JsonElement? Metadata) : MessagePart(MessagePartKind.Subtask);

/// <summary>Git patch/diff part showing file changes made by the agent.</summary>
public sealed record PatchPart(string? Patch) : MessagePart(MessagePartKind.Patch);

/// <summary>Normalized message from an agent conversation.</summary>
public sealed record HarnessMessage
{
    public required string Id { get; init; }
    public required string Role { get; init; }
    public required IReadOnlyList<MessagePart> Parts { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The agent that produced this message (e.g. "loom", "thread").</summary>
    public string? Agent { get; init; }

    /// <summary>The model that produced this message (e.g. "claude-sonnet-4").</summary>
    public string? ModelId { get; init; }

    /// <summary>The failure that ended this message, when the turn it belongs to failed.</summary>
    public TurnError? Error { get; init; }

    /// <summary>Why the model stopped producing this message (e.g. "stop", "length"), when reported.</summary>
    public string? Finish { get; init; }

    /// <summary>Convenience: concatenated text parts.</summary>
    public string TextContent =>
        string.Join("", Parts.OfType<TextPart>().Select(p => p.Text));
}

/// <summary>Query parameters for paginated message retrieval.</summary>
public sealed record MessageQuery(int? Limit = null, string? Before = null);

/// <summary>
/// A page of messages with a continuation flag. <paramref name="Cursor"/>, when the harness pages with its
/// own cursors, is what to pass as <see cref="MessageQuery.Before"/> for the next older page.
/// </summary>
public sealed record MessagePage(IReadOnlyList<HarnessMessage> Messages, bool HasMore, string? Cursor = null);

/// <summary>Result of a health check on a harness instance.</summary>
public sealed record HealthCheckResult(bool Healthy, string? Message);

/// <summary>A file or image attached to a prompt.</summary>
public sealed record HarnessAttachment(string Mime, string Filename, string Data);

/// <summary>Options for sending a prompt to an agent.</summary>
public sealed record PromptOptions
{
    public string? Agent { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public IReadOnlyList<HarnessAttachment>? Attachments { get; init; }
    public string? Effort { get; init; }
    public string? MessageId { get; init; }
}

/// <summary>Options for executing a slash command on an agent.</summary>
public sealed record CommandOptions
{
    /// <summary>Maximum allowed length for a command name.</summary>
    private const int MaxCommandLength = 64;

    /// <summary>Maximum allowed length for command arguments.</summary>
    private const int MaxArgumentsLength = 4096;

    public required string Command { get; init; }
    public string? Arguments { get; init; }
    public string? Agent { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }

    /// <summary>
    /// Validates the command name and arguments. Returns <c>null</c> when valid,
    /// or an error message describing the first violation found.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Command))
            return "Command name is required.";

        if (Command.Length > MaxCommandLength)
            return $"Command name exceeds {MaxCommandLength} characters.";

        foreach (var ch in Command)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not ('_' or '-' or ':'))
                return $"Command name contains invalid character '{ch}'. Only letters, digits, hyphens, underscores, and colons are allowed.";
        }

        if (Arguments is not null && Arguments.Length > MaxArgumentsLength)
            return $"Arguments exceed {MaxArgumentsLength} characters.";

        return null;
    }
}

/// <summary>An agent available for selection in the UI.</summary>
public sealed record AgentInfo
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Mode { get; init; }
    public bool Hidden { get; init; }
    public string? ModelProviderId { get; init; }
    public string? ModelId { get; init; }
}

/// <summary>A slash command available for selection in the UI.</summary>
public sealed record CommandInfo
{
    public required string Name { get; init; }
    public string? Description { get; init; }
}

/// <summary>A model provider with its available models.</summary>
public sealed record ProviderInfo
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public required IReadOnlyList<ModelInfo> Models { get; init; }
}

/// <summary>A model available within a provider.</summary>
public sealed record ModelInfo
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<string>? Variants { get; init; }
}

/// <summary>
/// The agents and models a harness offers in a folder, read before any session exists so a new session can
/// start with them. The defaults are what a prompt that names neither gets.
/// </summary>
public sealed record HarnessCatalog
{
    public required IReadOnlyList<AgentInfo> Agents { get; init; }
    public required IReadOnlyList<ProviderInfo> Providers { get; init; }

    /// <summary>The agent a prompt goes to when it names none; null when the harness doesn't say.</summary>
    public string? DefaultAgent { get; init; }

    /// <summary>
    /// The model a prompt gets when neither it nor its agent names one; null when only the harness knows
    /// (it may pick by recent use).
    /// </summary>
    public string? DefaultModelProviderId { get; init; }
    /// <inheritdoc cref="DefaultModelProviderId" />
    public string? DefaultModelId { get; init; }
}
