using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Skills;

/// <summary>Improve a skill from a session: what should change, and what the model may read to see why.</summary>
/// <param name="SessionId">The session the skill was used in. Its harness, folder and model ask the question.</param>
/// <param name="Note">What the user wants the skill to do differently.</param>
/// <param name="Context">Parts of the session the user chose to include, as text; null for none.</param>
/// <param name="WholeConversation">
/// Ask in a fork of the session, so the model reads all of it, instead of sending <paramref name="Context"/>.
/// </param>
public sealed record ImproveSkillRequest(string SessionId, string Note, string? Context = null, bool WholeConversation = false);

/// <summary>A change the model proposed. Nothing is saved until the user keeps it.</summary>
/// <param name="Base">The text the change was made to: the user's active version, or Fleet's.</param>
/// <param name="BaseVersion">The user's version it was made to; null for Fleet's.</param>
/// <param name="Asks">1, or 2 when the first answer broke the skill and the model was asked again.</param>
public sealed record SkillProposal(string Name, string Base, int? BaseVersion, string Content, int Asks, DraftTokensDto? Tokens);

/// <summary>
/// Asks the model for a better version of a built-in skill, off the record: nothing lands in any session's history.
/// By default the question has no session behind it and carries only what the user chose (the skill, their note, and
/// the parts of the session they ticked), on the session's harness, folder and model. With the whole conversation, it's
/// asked in a fork of the session instead. An answer that breaks the skill's front matter is asked about once more.
/// </summary>
public sealed partial class SkillImprover(
    ISessionRepository sessions,
    ISessionActivator activator,
    SessionActivityTracker activity,
    IHarnessRegistry harnesses,
    HarnessCatalogService catalogs,
    BuiltInSkillService skills,
    IUserContext user,
    ILogger<SkillImprover> logger)
{
    private const int MaxNoteLength = 2000;
    private const int MaxContextLength = 60_000;

    public async Task<Result<SkillProposal>> ImproveAsync(string name, ImproveSkillRequest request, CancellationToken ct)
    {
        var note = request.Note?.Trim();
        if (string.IsNullOrEmpty(note))
            return FleetError.ValidationError("Note", "Say what the skill should do differently.");
        if (note.Length > MaxNoteLength)
            return FleetError.ValidationError("Note", $"Keep the note under {MaxNoteLength} characters.");
        var context = request.WholeConversation ? null : request.Context?.Trim();
        if (context?.Length > MaxContextLength)
            return FleetError.ValidationError("Context", "That's too much of the session to send. Include less, or pick the whole conversation.");

        if (await skills.CurrentAsync(name).ConfigureAwait(false) is not { } current)
            return FleetError.NotFoundFor("BuiltInSkill", name);

        var session = await sessions.GetByIdAsync(request.SessionId).ConfigureAwait(false);
        if (session is null)
            return FleetError.NotFoundFor("Session", request.SessionId);

        var harness = harnesses.GetByType(session.HarnessType);
        if (harness?.Capabilities.SupportsOffTheRecordPrompt != true)
            return FleetError.ValidationError("HarnessType", $"Improve isn't available on {harness?.DisplayName ?? session.HarnessType}: it can't ask a question off the record.");

        var conversation = request.WholeConversation
            ? await ForkAsync(session.Id, session.RetentionStatus, ct).ConfigureAwait(false)
            : await AskAloneAsync(session, ct).ConfigureAwait(false);
        if (conversation.IsFailure)
            return conversation.Error;

        return await ProposeAsync(conversation.Value, name, current.Content, current.Version, note, context, ct).ConfigureAwait(false);
    }

    /// <summary>The session's own conversation, read from the provider's cache, as "Save as workflow…" does.</summary>
    private async Task<Result<IOffTheRecordConversation>> ForkAsync(string sessionId, string retention, CancellationToken ct)
    {
        if (string.Equals(retention, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session", "Restore this session first: it's archived.");
        if (SessionActivityTracker.IsInTurn(activity.GetEffectiveActivityStatus(sessionId)))
            return FleetError.ValidationError("Session", "Wait for this session's turn to end, or don't include the whole conversation.");

        var instance = await activator.ActivateSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (instance.IsFailure)
            return instance.Error;

        return await instance.Value.StartOffTheRecordAsync(ct).ConfigureAwait(false) is { } conversation
            ? Result.Success(conversation)
            : FleetError.ValidationError("Session", "This session has no conversation to read yet.");
    }

    /// <summary>A question with nothing behind it, on the session's harness, folder, profile and model.</summary>
    private async Task<Result<IOffTheRecordConversation>> AskAloneAsync(Domain.Entities.Session session, CancellationToken ct)
    {
        if (harnesses.GetRuntimeByType(session.HarnessType) is not { } runtime)
            return FleetError.ValidationError("HarnessType", $"Improve isn't available on {session.HarnessType}.");
        if (string.IsNullOrWhiteSpace(session.Directory) || !Directory.Exists(session.Directory))
            return FleetError.ValidationError("Session", "This session's folder is gone, so the question has nowhere to run.");

        var profile = await catalogs.ResolveProfileAsync(session.HarnessType, session.HarnessProfileId).ConfigureAwait(false);
        if (profile.IsFailure)
            return profile.Error;

        var (providerId, modelId) = string.IsNullOrWhiteSpace(session.SelectedProviderId) || string.IsNullOrWhiteSpace(session.SelectedModelId)
            ? (null, null)
            : (session.SelectedProviderId, session.SelectedModelId);
        var conversation = await runtime.StartOffTheRecordAsync(
            new OffTheRecordOptions(user.UserId, session.Directory, profile.Value, providerId, modelId, null),
            ct).ConfigureAwait(false);
        return conversation is null
            ? FleetError.ValidationError("HarnessType", "This harness can't ask the model here. On OpenCode, turn on pooled mode in Settings.")
            : Result.Success<IOffTheRecordConversation>(conversation);
    }

    /// <summary>Asks, checks, and asks once more with the problem if the answer breaks the skill. Always disposes.</summary>
    internal async Task<Result<SkillProposal>> ProposeAsync(
        IOffTheRecordConversation conversation, string name, string current, int? version, string note, string? context, CancellationToken ct)
    {
        await using (conversation.ConfigureAwait(false))
        {
            try
            {
                var first = await conversation.AskAsync(SkillImprovePrompt.Improve(name, current, note, context), ct).ConfigureAwait(false);
                if (first is null)
                    return NoAnswer();

                var content = SkillImprovePrompt.SkillIn(first.Text);
                var tokens = first.Tokens;
                var asks = 1;
                if (SkillVersions.Problem(name, content) is { } problem)
                {
                    var second = await conversation.AskAsync(SkillImprovePrompt.Retry(problem), ct).ConfigureAwait(false);
                    if (second is null)
                        return NoAnswer();
                    content = SkillImprovePrompt.SkillIn(second.Text);
                    tokens = OffTheRecordTokens.Add(first.Tokens, second.Tokens);
                    asks = 2;
                    if (SkillVersions.Problem(name, content) is { } again)
                        return FleetError.ValidationError("Model", $"The model's answer broke the skill: {again} Try again, or edit it by hand.");
                }

                if (SkillVersions.Normalize(content!) == SkillVersions.Normalize(current))
                    return FleetError.ValidationError("Model", "The model didn't change anything. Say more about what should be different.");

                return new SkillProposal(
                    name,
                    current,
                    version,
                    content!,
                    asks,
                    tokens is null ? null : new DraftTokensDto(tokens.Total, tokens.FromCache));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return FleetError.ValidationError("Model", "The model took too long to answer. Try again.");
            }
            catch (HttpRequestException ex)
            {
                LogAskFailed(ex);
                return FleetError.ValidationError("Model", $"The harness couldn't ask the model: {ex.Message}");
            }
        }
    }

    private static FleetError NoAnswer() => FleetError.ValidationError("Model", "The model didn't answer with a skill. Try again.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't ask the model to improve a skill")]
    private partial void LogAskFailed(Exception ex);
}

/// <summary>The question that improves a skill, and reading the skill out of the answer.</summary>
public static class SkillImprovePrompt
{
    private const string Open = "<skill>";
    private const string Close = "</skill>";

    public static string Improve(string name, string current, string note, string? context)
    {
        var what = string.IsNullOrWhiteSpace(context)
            ? string.Empty
            : $"""

              What happened when it was used, from the session:

              <session>
              {context}
              </session>

              """;

        return $"""
            You're improving {name}, a skill: instructions an agent loads to do one kind of job. The user used it and
            wants it to work differently next time.

            What the user wants changed:

            {note}
            {what}
            The skill's SKILL.md now:

            {Open}
            {current.TrimEnd()}
            {Close}

            Rules:
            - Change only what the user asked for. Keep everything else word for word, including its headings, examples
              and code blocks.
            - Write the change as guidance for every future use, not about this one case. Don't name files, values or
              findings from the session.
            - Keep the skill's voice: short, plain sentences.
            - Keep the front matter's name: {name}. Change its description only if the change affects when the skill
              should be used, and keep it on one line.
            - Answer with the whole new SKILL.md between {Open} and {Close}, and nothing else.
            """;
    }

    public static string Retry(string problem) =>
        $"That skill can't be used: {problem} Answer with the whole SKILL.md again, corrected, between {Open} and {Close}.";

    /// <summary>
    /// The skill in an answer: between the tags, else a whole answer that starts with front matter, else the answer
    /// without a fence around it. Null when there's nothing.
    /// </summary>
    public static string? SkillIn(string answer)
    {
        var start = answer.IndexOf(Open, StringComparison.Ordinal);
        if (start >= 0)
        {
            var body = answer[(start + Open.Length)..];
            var end = body.LastIndexOf(Close, StringComparison.Ordinal);
            return Trimmed(end >= 0 ? body[..end] : body);
        }

        // A fence around the whole answer: its closing fence is the last one as long as the opening, since the skill
        // can have code blocks of its own.
        var text = answer.Trim();
        var ticks = text.TakeWhile(c => c == '`').Count();
        if (ticks >= 3)
        {
            var firstLine = text.IndexOf('\n');
            var last = text.LastIndexOf(new string('`', ticks), StringComparison.Ordinal);
            if (firstLine > 0 && last > firstLine)
                text = text[(firstLine + 1)..last];
        }

        return Trimmed(text);
    }

    private static string? Trimmed(string text)
    {
        var trimmed = text.Trim('\r', '\n', ' ');
        return trimmed.Length == 0 ? null : trimmed + "\n";
    }
}
