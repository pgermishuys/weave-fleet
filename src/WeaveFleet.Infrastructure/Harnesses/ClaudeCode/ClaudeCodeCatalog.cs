using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// The models Claude Code offers, with the reasoning efforts each takes, for the model picker. They come from Claude
/// Code itself: a <c>claude</c> process answers the <c>initialize</c> request with its <c>models</c>, which include the
/// ones a gateway offers (<c>CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY</c>), before any prompt and without calling a
/// model. The same answer lists its commands and skills, for the composer's <c>/</c>. Fleet asks once per folder (a
/// project's settings can change them) and set of built-in skills, and keeps the answer for <see cref="CacheFor"/>. When Claude
/// Code can't say, the picker offers <see cref="Fallback"/> and the composer no commands.
/// </summary>
internal sealed partial class ClaudeCodeCatalog(ClaudeCodeOptions config, ILoggerFactory loggerFactory)
{
    /// <summary>The one provider Fleet lists Claude Code's models under.</summary>
    internal const string ProviderId = "claude-code";

    /// <summary>How long a folder's list is kept.</summary>
    internal static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(20);

    private static readonly IReadOnlyList<string> Efforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Claude Code's model aliases, for when it can't be asked.</summary>
    internal static readonly IReadOnlyList<ProviderInfo> Fallback =
    [
        new ProviderInfo
        {
            Id = ProviderId,
            Name = "Claude Code",
            Models =
            [
                new ModelInfo { Id = "opus", Name = "Opus", Variants = Efforts },
                new ModelInfo { Id = "sonnet", Name = "Sonnet", Variants = Efforts },
                new ModelInfo { Id = "haiku", Name = "Haiku" },
            ],
        },
    ];

    // By folder and the built-in skills turned on: a project's settings, and those skills, change the answer.
    private readonly ConcurrentDictionary<(string Directory, string? Skills), (DateTimeOffset At, Task<JsonElement?> Answer)> _answers = new();

    private readonly ILogger _logger = loggerFactory.CreateLogger<ClaudeCodeCatalog>();

    /// <summary>The clock for the cache; the system's unless a test says otherwise.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Asks a claude process in a folder, with Fleet's skills folder, what it offers; Fleet's unless a test says otherwise.</summary>
    internal Func<string, string?, CancellationToken, Task<JsonElement?>>? Ask { get; init; }

    /// <summary>The models Claude Code offers in <paramref name="directory"/>, or <see cref="Fallback"/>.</summary>
    public async Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(string directory, CancellationToken ct, ClaudeCodeSkillsFolder? skills = null)
        => await AnswerAsync(directory, skills, ct).ConfigureAwait(false) is { } answer && FromInitialize(answer) is { } models
            ? models
            : Fallback;

    /// <summary>The commands and skills Claude Code offers in <paramref name="directory"/>; none when it can't say.</summary>
    public async Task<IReadOnlyList<ClaudeCodeCommand>> GetCommandsAsync(string directory, ClaudeCodeSkillsFolder? skills, CancellationToken ct)
        => await AnswerAsync(directory, skills, ct).ConfigureAwait(false) is { } answer ? CommandsFromInitialize(answer) : [];

    private async Task<JsonElement?> AnswerAsync(string directory, ClaudeCodeSkillsFolder? skills, CancellationToken ct)
    {
        var now = Time.GetUtcNow();
        var key = (directory, skills?.Skills);
        var entry = _answers.AddOrUpdate(
            key,
            _ => (now, LoadAsync(directory, skills?.Folder)),
            (_, known) => now - known.At < CacheFor && !known.Answer.IsFaulted ? known : (now, LoadAsync(directory, skills?.Folder)));

        var answer = await entry.Answer.WaitAsync(ct).ConfigureAwait(false);
        if (answer is null)
        {
            // Asked again next time rather than kept.
            _answers.TryRemove(new KeyValuePair<(string, string?), (DateTimeOffset, Task<JsonElement?>)>(key, entry));
        }

        return answer;
    }

    private async Task<JsonElement?> LoadAsync(string directory, string? skillsDirectory)
    {
        try
        {
            return await (Ask ?? AskClaudeAsync)(directory, skillsDirectory, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogAskFailed(_logger, directory, ex);
            return null;
        }
    }

    /// <summary>
    /// The <c>commands</c> in an <c>initialize</c> answer: each one's <c>name</c>, <c>description</c>, and whether it's
    /// <c>builtin</c> (Claude Code's own, its bundled skills and its terminal commands alike) rather than the user's, a
    /// project's, a plugin's or Fleet's built-in skills. Names starting <c>__</c> are Claude Code's internals.
    /// </summary>
    internal static IReadOnlyList<ClaudeCodeCommand> CommandsFromInitialize(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("commands", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var commands = new List<ClaudeCodeCommand>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in list.EnumerateArray())
        {
            if (PermissionEvents.String(command, "name") is not { Length: > 0 } name || name.StartsWith("__", StringComparison.Ordinal) || !seen.Add(name))
                continue;

            var builtIn = command.TryGetProperty("builtin", out var flag) && flag.ValueKind == JsonValueKind.True;
            commands.Add(new ClaudeCodeCommand(name, PermissionEvents.String(command, "description"), builtIn));
        }

        return commands;
    }

    /// <summary>
    /// The models in an <c>initialize</c> answer: each one's <c>value</c> (what <c>--model</c> and <c>set_model</c>
    /// take), <c>displayName</c> and <c>supportedEffortLevels</c>. <c>default</c> is left out: Fleet's own Default
    /// stands for it. Null when the answer lists none.
    /// </summary>
    internal static IReadOnlyList<ProviderInfo>? FromInitialize(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("models", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var models = new List<ModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in list.EnumerateArray())
        {
            if (PermissionEvents.String(model, "value") is not { Length: > 0 } id || id == "default" || !seen.Add(id))
                continue;

            List<string>? efforts = null;
            if (model.TryGetProperty("supportedEffortLevels", out var levels) && levels.ValueKind == JsonValueKind.Array)
            {
                efforts = [.. levels.EnumerateArray().Where(level => level.ValueKind == JsonValueKind.String).Select(level => level.GetString()!)];
            }

            models.Add(new ModelInfo
            {
                Id = id,
                Name = PermissionEvents.String(model, "displayName") ?? id,
                Variants = efforts is { Count: > 0 } ? efforts : null,
            });
        }

        return models.Count == 0 ? null : [new ProviderInfo { Id = ProviderId, Name = "Claude Code", Models = models }];
    }

    /// <summary>
    /// Starts <c>claude</c> in <paramref name="directory"/> the way a session would, sends <c>initialize</c>, and stops it
    /// once it answered. No prompt is sent, so no model is called.
    /// </summary>
    private async Task<JsonElement?> AskClaudeAsync(string directory, string? skillsDirectory, CancellationToken ct)
    {
        await using var process = new ClaudeCodeProcessManager(loggerFactory.CreateLogger<ClaudeCodeProcessManager>());
        var stdout = await process.StartAsync(new ClaudeCodeProcessOptions
        {
            BinaryPath = config.BinaryPath,
            WorkingDirectory = directory,
            PermissionMode = "default",
            SkillsDirectory = skillsDirectory,
        }, ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AskTimeout);
        var reading = Task.Run(async () =>
        {
            await foreach (var message in ClaudeCodeStdioClient.ReadMessagesAsync(stdout, _logger, timeout.Token).ConfigureAwait(false))
            {
                if (message is ClaudeCodeControlResponse { Response: { } response })
                    process.CompleteRequest(response);
            }
        }, CancellationToken.None);

        var requestId = $"fleet-{Guid.NewGuid():N}";
        var answer = await process.RequestAsync(requestId, ClaudeCodeInput.Initialize(requestId), AskTimeout, timeout.Token).ConfigureAwait(false);
        await process.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await timeout.CancelAsync().ConfigureAwait(false);
        try
        {
            await reading.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Its output never closed; the process is gone either way.
        }

        return answer is { Subtype: "success", Response: { } response } ? response.Clone() : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't ask Claude Code which models it offers in {Directory}; the picker offers Claude Code's aliases")]
    private static partial void LogAskFailed(ILogger logger, string directory, Exception exception);
}

/// <summary>A command or skill a claude process offers: <see cref="BuiltIn"/> for Claude Code's own.</summary>
internal sealed record ClaudeCodeCommand(string Name, string? Description, bool BuiltIn);
