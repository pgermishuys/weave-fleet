using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Tool calls a turn left behind. A call is only ever running inside a turn, so when a session isn't in one, a call
/// still marked pending or running was cut off: Fleet or the harness stopped, or the machine went away, before the
/// call's end reached the history. Nobody will ever finish it, and shown as it was it keeps a working dot on the call
/// forever, after a restart and on every reload. It is shown as failed instead.
/// <para>
/// Two kinds of call legitimately outlive their turn and are left alone: a call the harness moved into the
/// background (it says so, <see cref="ToolRunningState.Background"/>), and a sub-agent call whose child session is
/// still working or was moved into the background.
/// </para>
/// </summary>
public static class CutOffToolCalls
{
    /// <summary>The error a cut-off call shows.</summary>
    public const string Message = "The turn ended before this call finished.";

    /// <summary>
    /// <paramref name="messages"/> with every cut-off call shown as failed. <paramref name="stillWorking"/> says, by call
    /// id, which calls carry on outside the turn.
    /// </summary>
    public static IReadOnlyList<MessageLifecyclePayload> Settle(
        IReadOnlyList<MessageLifecyclePayload> messages,
        Func<string, bool> stillWorking)
    {
        List<MessageLifecyclePayload>? settled = null;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var parts = SettleParts(message.Parts, stillWorking);
            if (parts is null)
                continue;

            settled ??= [.. messages];
            settled[i] = message with { Parts = parts };
        }

        return settled ?? messages;
    }

    /// <summary>The same for history read as harness messages.</summary>
    public static IReadOnlyList<HarnessMessage> Settle(IReadOnlyList<HarnessMessage> messages, Func<string, bool> stillWorking)
    {
        List<HarnessMessage>? settled = null;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            List<MessagePart>? parts = null;
            for (var j = 0; j < message.Parts.Count; j++)
            {
                if (message.Parts[j] is not ToolUsePart tool || !IsCutOff(tool, stillWorking))
                    continue;

                parts ??= [.. message.Parts];
                parts[j] = tool with { State = ToolUseState.Error, Error = tool.Error ?? Message };
            }

            if (parts is null)
                continue;

            settled ??= [.. messages];
            settled[i] = message with { Parts = parts };
        }

        return settled ?? messages;
    }

    /// <summary>
    /// Which calls carry on outside the turn: sub-agent calls whose child session is working, waits on the user, or
    /// was moved into the background.
    /// </summary>
    public static Func<string, bool> StillWorking(
        IEnumerable<(string? ParentToolCallId, string? ChildSessionId)> delegations,
        Func<string, bool> childStillWorking)
    {
        var working = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (callId, childSessionId) in delegations)
        {
            if (callId is not null && childSessionId is not null && childStillWorking(childSessionId))
                working.Add(callId);
        }

        return working.Contains;
    }

    private static List<MessageEventPart>? SettleParts(IReadOnlyList<MessageEventPart> parts, Func<string, bool> stillWorking)
    {
        List<MessageEventPart>? settled = null;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not ToolMessageEventPart tool)
                continue;

            ToolErrorState? error = tool.State switch
            {
                ToolPendingState pending when !stillWorking(tool.CallId) =>
                    new ToolErrorState { Input = pending.Input, Error = Message },
                ToolRunningState { Background: false } running when !stillWorking(tool.CallId) =>
                    new ToolErrorState { Input = running.Input, Output = running.Output, Metadata = running.Metadata, Error = Message },
                _ => null,
            };
            if (error is null)
                continue;

            settled ??= [.. parts];
            settled[i] = tool with { State = error };
        }

        return settled;
    }

    private static bool IsCutOff(ToolUsePart tool, Func<string, bool> stillWorking)
        => tool.State is ToolUseState.Pending || (tool.State is ToolUseState.Running && !tool.Background)
            ? !stillWorking(tool.ToolCallId)
            : false;
}
