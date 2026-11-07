using System.Text;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// The agent hand-off tools, for calls from a harness process: <c>fleet_machine_list</c>, <c>fleet_session_start</c>, and
/// <c>fleet_message</c> / <c>fleet_session_read</c> naming a machine. The caller is the session the call resolves to through
/// <see cref="IHarnessCanvasCallerResolver"/>, and the message's sender is that session on this machine; the agent names
/// neither. Each machine's own setting decides, checked on every call (<see cref="RemoteMachine.AgentWork"/>): a machine set
/// to Never isn't there for the agent, one set to Ask has Fleet ask the user in the conversation first
/// (<see cref="MachineHandoffAsks"/>), and the call waits for the answer.
/// </summary>
public sealed class MachineHandoffBridge(
    IEnumerable<IHarnessCanvasCallerResolver> callers,
    IBackgroundUserScope userScope,
    SessionService sessions,
    ISessionRepository sessionRepository,
    IUserPreferenceRepository preferences,
    MachineHandoffAsks asks,
    RemoteSessions remote,
    MachineIdentityStore identity,
    SessionUpdates updates,
    IRemoteSessionEvents events,
    RecentSessionStarts recentStarts)
{
    public const string NoMachinesMessage =
        "No machine takes work from agents here. The user decides it per machine in Settings › Machines.";

    /// <summary>What the model reads when it has to call again to keep waiting for the user (a harness that cuts a held call).</summary>
    public const string StillWaitingMessage =
        "Fleet asked the user and they haven't answered yet. Call this tool again with the same arguments to keep waiting for their answer.";

    /// <summary><c>fleet_machine_list</c>: the machines the agent may hand work to, and what each can run.</summary>
    public Task<CanvasResult<CanvasToolOutput>> ListAsync(string? bridgeToken, string? harnessSessionId, CancellationToken ct = default)
        => AsCallerAsync(bridgeToken, harnessSessionId, async _ =>
        {
            var machines = await remote.AllowedAsync().ConfigureAwait(false);
            if (machines.Count == 0)
                return CanvasResult.Ok(new CanvasToolOutput("No machines", NoMachinesMessage));

            var output = new StringBuilder("Machines you can hand work to:\n");
            foreach (var machine in machines)
            {
                output.Append('\n').Append(machine.Name);
                if (machine.Os is { Length: > 0 } os)
                    output.Append(" · ").Append(os);
                if (machine.AgentWork == MachineAgentWork.Ask)
                    output.Append(" · Fleet asks the user before you send it work");

                var token = await remote.FindAsync(machine.Id).ConfigureAwait(false);
                if (token.Token is null)
                {
                    output.Append(" · ").Append(token.Error).Append('\n');
                    continue;
                }

                var view = await remote.DescribeAsync(machine, token.Token, ct).ConfigureAwait(false);
                if (view.Away is not null)
                {
                    output.Append(" · not answering: ").Append(view.Away).Append('\n');
                    continue;
                }

                if (!view.PeerMessages)
                {
                    output.Append(" · ").Append(TooOld(machine.Name)).Append('\n');
                    continue;
                }

                output.Append(" · answering\n")
                    .Append("  Harnesses: ").Append(view.Harnesses.Count == 0 ? "none ready" : string.Join(", ", view.Harnesses)).Append('\n')
                    .Append("  Folders: ").Append(view.Folders.Count == 0 ? "none found" : string.Join(", ", view.Folders)).Append('\n');
            }

            return CanvasResult.Ok(new CanvasToolOutput($"Listed {machines.Count} {(machines.Count == 1 ? "machine" : "machines")}", output.ToString().TrimEnd()));
        });

    /// <summary>
    /// <c>fleet_session_start</c>: an empty session on the machine, in a fresh worktree of <paramref name="branch"/> or in
    /// the folder as it is, then the task as a message from the calling session.
    /// </summary>
    public Task<CanvasResult<CanvasToolOutput>> StartAsync(
        string? bridgeToken,
        string? harnessSessionId,
        string? machineName,
        string? folder,
        string? title,
        string? task,
        string? branch,
        string? harness,
        bool notifyWhenDone = false,
        CancellationToken ct = default)
        => AsCallerAsync(bridgeToken, harnessSessionId, async caller =>
        {
            if (string.IsNullOrWhiteSpace(machineName))
                return Invalid("\"machine\" is required: a machine's name from fleet_machine_list.");
            if (string.IsNullOrWhiteSpace(folder))
                return Invalid("\"folder\" is required: a folder on that machine, from fleet_machine_list.");
            if (string.IsNullOrWhiteSpace(title))
                return Invalid("\"title\" is required.");
            if (string.IsNullOrWhiteSpace(task))
                return Invalid("\"task\" is required.");

            if (notifyWhenDone && updates.IsStartedByUpdate(caller.FleetSessionId))
                return Refused(SessionMessageBridge.NoNotifyFromUpdateMessage);

            var (machine, token, error) = await remote.FindAsync(machineName).ConfigureAwait(false);
            if (machine is null)
                return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, error!);

            // An older Fleet there would start the session and then not take the task.
            var view = await remote.DescribeAsync(machine, token!, ct).ConfigureAwait(false);
            if (view.Away is not null)
                return Refused(view.Away);
            if (!view.PeerMessages)
                return Refused(TooOld(machine.Name));

            var sender = await SenderAsync(caller).ConfigureAwait(false);
            if (sender is null)
                return UnknownCaller();

            var cleanBranch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
            var cleanHarness = string.IsNullOrWhiteSpace(harness) ? null : harness.Trim();

            // The tool call that timed out may still be running, and the agent calls again: the same start from the same
            // session within a few minutes is the one already made, not a second session.
            var key = string.Join('\u001f', caller.FleetSessionId, machine.Id, folder.Trim(), title.Trim(), task.Trim(), cleanBranch, cleanHarness, notifyWhenDone);
            var details = new HandoffAskDetails
            {
                Machine = machine.Name,
                Os = machine.Os,
                Folder = folder.Trim(),
                Branch = cleanBranch,
                Harness = cleanHarness,
                Task = FirstLine(task),
                Level = view.LevelFor(cleanHarness),
            };
            if (await AskFirstAsync(caller, machine, "start" + '\u001f' + key, "fleet_session_start", $"Start a session on {machine.Name}", () => Task.FromResult(details), ct).ConfigureAwait(false) is { } unanswered)
                return unanswered;

            return await recentStarts.OnceAsync(key, () => StartOnceAsync(caller, machine, token!, folder.Trim(), title.Trim(), task.Trim(), cleanBranch, cleanHarness, notifyWhenDone, sender, ct)).ConfigureAwait(false);
        });

    private async Task<CanvasResult<CanvasToolOutput>> StartOnceAsync(
        HarnessCanvasCaller caller,
        RemoteMachine machine,
        string token,
        string folder,
        string title,
        string task,
        string? cleanBranch,
        string? cleanHarness,
        bool notifyWhenDone,
        RemotePeerSender sender,
        CancellationToken ct)
    {
            var (started, startError) = await remote.StartAsync(machine, token, folder, title, cleanBranch, cleanHarness, ct).ConfigureAwait(false);
            if (started is null)
                return Refused($"Fleet couldn't start it: {startError}");

            var (delivered, messageError) = await remote.MessageAsync(machine, token, started.SessionId, sender, task, ct).ConfigureAwait(false);
            if (delivered is null)
            {
                return Refused(
                    $"Started {started.Title} on {machine.Name} ({started.SessionId}), but couldn't give it the task: {messageError} "
                    + $"Send it with fleet_message, machine {machine.Name}.");
            }

            asks.Grant(caller.FleetSessionId, machine.Id, started.SessionId);
            var watching = notifyWhenDone && Watch(caller, machine, started.SessionId, started.Title, delivered.MessageId);
            var where = started.Branch is { } onBranch ? $" in a worktree on {onBranch}" : $" in {folder}";
            return CanvasResult.Ok(new CanvasToolOutput(
                $"Started {started.Title} on {machine.Name}",
                $"Started {started.Title} on {machine.Name} ({started.SessionId}){where}, and gave it the task. It's working on it now. "
                + ReplyNote(watching, machine.Name)));
    }

    /// <summary><c>fleet_message</c> naming a machine, once <see cref="Sessions.SessionMessageBridge"/> has placed the caller.</summary>
    public Task<CanvasResult<CanvasToolOutput>> MessageAsync(
        HarnessCanvasCaller caller,
        string machineName,
        string toSessionId,
        string text,
        bool notifyWhenDone,
        CancellationToken ct = default)
        => WithMachineAsync(caller, machineName, async (machine, token) =>
        {
            if (notifyWhenDone && updates.IsStartedByUpdate(caller.FleetSessionId))
                return Refused(SessionMessageBridge.NoNotifyFromUpdateMessage);

            var sender = await SenderAsync(caller).ConfigureAwait(false);
            if (sender is null)
                return UnknownCaller();

            // A session the agent started or messaged there already needs no second ask.
            if (!asks.IsGranted(caller.FleetSessionId, machine.Id, toSessionId))
            {
                var key = string.Join('\u001f', "message", caller.FleetSessionId, machine.Id, toSessionId, text.Trim());
                var asked = await AskFirstAsync(caller, machine, key, "fleet_message", $"Message a session on {machine.Name}", async () =>
                {
                    var view = await remote.DescribeAsync(machine, token, ct).ConfigureAwait(false);
                    return new HandoffAskDetails { Machine = machine.Name, Os = machine.Os, Session = toSessionId, Task = FirstLine(text), Level = view.LevelFor(null) };
                }, ct).ConfigureAwait(false);
                if (asked is not null)
                    return asked;
            }

            var (delivered, error) = await remote.MessageAsync(machine, token, toSessionId, sender, text.Trim(), ct).ConfigureAwait(false);
            if (delivered is null)
                return Refused($"Fleet couldn't deliver it: {error}");

            asks.Grant(caller.FleetSessionId, machine.Id, toSessionId);

            var watching = notifyWhenDone && Watch(caller, machine, toSessionId, delivered.Title, delivered.MessageId);
            return CanvasResult.Ok(new CanvasToolOutput(
                $"Messaged {delivered.Title} on {machine.Name}",
                $"Delivered to {delivered.Title} on {machine.Name} ({toSessionId}). It starts on it now, or when the turn it's on ends. "
                + ReplyNote(watching, machine.Name)));
        });

    /// <summary><c>fleet_session_read</c> naming a machine, once <see cref="Sessions.SessionReadBridge"/> has placed the caller.</summary>
    public Task<CanvasResult<CanvasToolOutput>> ReadAsync(
        HarnessCanvasCaller caller,
        string machineName,
        string sessionId,
        string? before,
        int? limit,
        CancellationToken ct = default)
        => WithMachineAsync(caller, machineName, async (machine, token) =>
        {
            var (page, error) = await remote.PageAsync(machine, token, sessionId, before, limit, ct).ConfigureAwait(false);
            return page is null
                ? Refused($"Fleet couldn't read it: {error}")
                : CanvasResult.Ok(new CanvasToolOutput($"{page.Title} on {machine.Name}", page.Text));
        });

    private async Task<CanvasResult<CanvasToolOutput>> AsCallerAsync(
        string? bridgeToken,
        string? harnessSessionId,
        Func<HarnessCanvasCaller, Task<CanvasResult<CanvasToolOutput>>> run)
    {
        if (string.IsNullOrWhiteSpace(bridgeToken) || string.IsNullOrWhiteSpace(harnessSessionId))
            return UnknownCaller();

        var caller = await callers.ResolveAsync(bridgeToken, harnessSessionId).ConfigureAwait(false);
        if (caller is null)
            return UnknownCaller();

        using (userScope.Begin(caller.UserId))
            return await run(caller).ConfigureAwait(false);
    }

    private async Task<CanvasResult<CanvasToolOutput>> WithMachineAsync(
        HarnessCanvasCaller caller,
        string machineName,
        Func<RemoteMachine, string, Task<CanvasResult<CanvasToolOutput>>> run)
    {
        using (userScope.Begin(caller.UserId))
        {
            var (machine, token, error) = await remote.FindAsync(machineName).ConfigureAwait(false);
            if (machine is null)
                return CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, error!);
            return await run(machine, token!).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the call may go ahead on <paramref name="machine"/>: null when it may, otherwise what the agent reads. A
    /// machine set to Ask has Fleet ask the user in the conversation and wait for the answer, a round at a time; in a run
    /// nobody watches, what Settings › Permissions says for unattended runs decides instead.
    /// </summary>
    private async Task<CanvasResult<CanvasToolOutput>?> AskFirstAsync(
        HarnessCanvasCaller caller,
        RemoteMachine machine,
        string key,
        string tool,
        string title,
        Func<Task<HandoffAskDetails>> details,
        CancellationToken ct)
    {
        if (machine.AgentWork == MachineAgentWork.Always)
            return null;

        if (!asks.IsWaiting(caller.FleetSessionId, key))
        {
            switch (await UnattendedAsync(caller).ConfigureAwait(false))
            {
                case UnattendedPermissions.All:
                    return null;
                case UnattendedPermissions.Deny:
                    return Refused(
                        $"Fleet asks the user before an agent sends work to {machine.Name}, and this run has nobody watching it: "
                        + "its permission settings refuse anything that would need asking. Do it here, or stop and say what you needed.");
            }

            var ask = new PermissionAsk
            {
                Id = MachineHandoffAsks.IdPrefix + Guid.NewGuid().ToString("N"),
                SessionId = caller.FleetSessionId,
                Kind = PermissionKinds.Other,
                Tool = tool,
                Title = title,
                Handoff = await details().ConfigureAwait(false),
            };
            if (!asks.Ask(key, machine.Id, ask))
                return Refused($"Fleet couldn't ask the user about {machine.Name}: nothing serves this session right now.");
        }

        return await asks.NextAsync(caller.FleetSessionId, key, MachineHandoffAsks.Round, ct).ConfigureAwait(false) switch
        {
            null => CanvasResult.Ok(new CanvasToolOutput("Waiting for the user", StillWaitingMessage, Choice: PageChoiceOutput.StillWaiting)),
            PermissionReplies.Once or PermissionReplies.Always => null,
            PermissionReplies.Reject => Refused($"The user said no to sending work to {machine.Name}. Don't hand it work; do it here, or ask them what they'd like."),
            _ => Refused($"The user didn't answer about {machine.Name}: the ask ended with the turn. Ask them in your reply."),
        };
    }

    /// <summary>What Settings › Permissions says for runs nobody watches, when the caller is one; null when someone watches.</summary>
    private async Task<string?> UnattendedAsync(HarnessCanvasCaller caller)
    {
        if (await sessionRepository.GetByIdAsync(caller.FleetSessionId).ConfigureAwait(false) is not { } session
            || !await SessionPermissions.IsUnattendedAsync(sessionRepository, session).ConfigureAwait(false))
            return null;
        var value = await preferences.GetAsync(SessionPermissions.UnattendedKey).ConfigureAwait(false);
        return UnattendedPermissions.IsKnown(value) ? value : UnattendedPermissions.All;
    }

    private static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    /// <summary>The calling session on this machine, as the other machine records it.</summary>
    private async Task<RemotePeerSender?> SenderAsync(HarnessCanvasCaller caller)
    {
        var session = await sessions.GetSessionAsync(caller.FleetSessionId).ConfigureAwait(false);
        if (session.IsFailure)
            return null;

        var self = identity.Get();
        return new RemotePeerSender(self.Id, self.Name ?? Environment.MachineName, caller.FleetSessionId, session.Value.Title);
    }

    /// <summary>
    /// Asks to hear when the turn that handles the message ends there, as <c>fleet_message</c> does for a session here:
    /// this Fleet follows that session's events until it's told (<see cref="RemoteSessionTurns"/>).
    /// False when that machine's harness gave the message no id, so there's no telling which turn answers it.
    /// </summary>
    private bool Watch(HarnessCanvasCaller caller, RemoteMachine machine, string sessionId, string title, string? messageId)
    {
        if (messageId is null)
            return false;

        updates.Watch(new SessionUpdateWatch(
            caller.FleetSessionId, sessionId, caller.UserId, messageId, new SessionMessageMachine(machine.Id, machine.Name), title));
        events.Follow(machine.Id, sessionId);
        return true;
    }

    private static string ReplyNote(bool watching, string machine) => watching
        ? "Fleet will send you its reply when it's done, as a new message; you don't need to check on it."
        : $"Its reply stays in that session: read it with fleet_session_read, machine {machine}.";

    private static string TooOld(string machine) => $"Fleet on {machine} is too old to take work from an agent here. Update it there.";

    private static CanvasResult<CanvasToolOutput> UnknownCaller()
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.NotFound, CanvasBridge.UnknownCallerMessage);

    private static CanvasResult<CanvasToolOutput> Invalid(string message)
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Invalid, message);

    private static CanvasResult<CanvasToolOutput> Refused(string message)
        => CanvasResult.Fail<CanvasToolOutput>(CanvasErrorKind.Refused, message);
}
