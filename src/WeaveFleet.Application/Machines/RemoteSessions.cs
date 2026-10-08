using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// Sessions on another machine in this Fleet's list, for an agent here that hands work there: what the machine can run,
/// starting a session on it, and messaging and reading a session through its peer endpoints (<c>/api/machine/peer</c>).
/// Every call goes with the token this Fleet keeps for the machine, through <see cref="RemoteMachineRequests"/>.
/// </summary>
public sealed class RemoteSessions(RemoteMachineService machines, IHttpClientFactory httpClients)
{
    /// <summary>
    /// The client that starts sessions: it gives up at once on a machine that isn't there, but waits for one that is while
    /// it fetches the branch, makes the worktree and starts the harness.
    /// </summary>
    public const string HttpClientName = "RemoteSessions";

    /// <summary>The machines in the list that agents may hand work to.</summary>
    public async Task<IReadOnlyList<RemoteMachine>> AllowedAsync() => [.. (await machines.ListAsync()).Where(machine => machine.AgentsAllowed)];

    /// <summary>
    /// The allowed machine called <paramref name="nameOrId"/> (its name, any case, or its id) and its token; otherwise
    /// what to tell the agent.
    /// </summary>
    public async Task<(RemoteMachine? Machine, string? Token, string? Error)> FindAsync(string nameOrId)
    {
        var wanted = nameOrId.Trim();
        var allowed = await AllowedAsync();
        var byId = allowed.FirstOrDefault(machine => machine.Id == wanted);
        var byName = allowed.Where(machine => string.Equals(machine.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byId is null && byName.Count > 1)
            return (null, null, $"More than one machine is called {wanted}. Name it by its id: {string.Join(", ", byName.Select(m => m.Id))}.");

        var machine = byId ?? byName.FirstOrDefault();
        if (machine is null)
            return (null, null, $"You can't hand work to a machine called {wanted}. See the machines you can with fleet_machine_list.");

        var token = machines.TryTokenOf(machine);
        return token is null
            ? (null, null, $"Fleet can't read its token for {machine.Name}. The user enters it again in Settings › Machines.")
            : (machine, token, null);
    }

    /// <summary>What the machine says about itself (<c>GET /api/machine</c>) and its folders (<c>GET /api/repositories</c>).</summary>
    public async Task<RemoteMachineView> DescribeAsync(RemoteMachine machine, string token, CancellationToken ct)
    {
        var (identity, away, refused) = await GetAsync(machine, token, "/api/machine", ct);
        if (identity is null)
            return new RemoteMachineView(away ?? refused, PeerMessages: false, [], []);

        using (identity)
        {
            var capabilities = identity.RootElement.TryGetProperty("capabilities", out var found) && found.ValueKind == JsonValueKind.Object ? found : default;
            var peerMessages = capabilities.ValueKind == JsonValueKind.Object
                && capabilities.TryGetProperty("peerMessages", out var peer) && peer.ValueKind == JsonValueKind.True;
            List<string> harnesses = [];
            if (capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty("harnesses", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                harnesses.AddRange(list.EnumerateArray()
                    .Where(harness => IsTrue(harness, "available") && IsTrue(harness, "enabled"))
                    .Select(harness => Text(harness, "type"))
                    .OfType<string>());
            }

            List<string> folders = [];
            var (repositories, _, _) = await GetAsync(machine, token, "/api/repositories", ct);
            using (repositories)
            {
                if (repositories?.RootElement.TryGetProperty("repositories", out var repos) == true && repos.ValueKind == JsonValueKind.Array)
                    folders.AddRange(repos.EnumerateArray().Select(repo => Text(repo, "path")).OfType<string>());
            }

            return new RemoteMachineView(null, peerMessages, harnesses, folders);
        }
    }

    /// <summary>
    /// Starts an empty session in <paramref name="folder"/> on the machine: in a new worktree of
    /// <paramref name="branch"/>, fetched from origin there, or in the folder as it is when there's no branch.
    /// </summary>
    public async Task<(RemoteStarted? Started, string? Error)> StartAsync(
        RemoteMachine machine,
        string token,
        string folder,
        string title,
        string? branch,
        string? harness,
        CancellationToken ct)
    {
        var body = new RemoteSessionStart
        {
            Directory = folder,
            Title = title,
            IsolationStrategy = branch is null ? "existing" : "worktree",
            Branch = branch,
            HarnessType = harness,
            Source = branch is null
                ? new RemoteSource(
                    new RemoteSourceKey(SessionSourceProviderIds.Local, SessionSourceTypeNames.Directory),
                    new RemoteSourceInput { Directory = folder, IsolationStrategy = "existing" })
                : new RemoteSource(
                    new RemoteSourceKey(SessionSourceProviderIds.Repository, SessionSourceTypeNames.Repository),
                    new RemoteSourceInput { RepositoryPath = folder, IsolationStrategy = "worktree", Branch = branch, BaseBranch = $"origin/{branch}" }),
        };

        var (answer, away, refused) = await RemoteMachineRequests.SendAsync(
            httpClients, HttpClientName, machine, token, HttpMethod.Post, "/api/sessions", body, RemoteRunsJsonContext.Default.RemoteSessionStart, ct);
        if (answer is null)
            return (null, away ?? refused);

        using (answer)
        {
            var session = answer.RootElement.TryGetProperty("session", out var found) ? found : default;
            var id = session.ValueKind == JsonValueKind.Object ? Text(session, "id") : null;
            return id is null
                ? (null, $"{machine.Name} didn't say which session it started.")
                : (new RemoteStarted(id, Text(session, "title") ?? title, Text(answer.RootElement, "branch")), null);
        }
    }

    /// <summary>Delivers a message from <paramref name="from"/> to a session on the machine (its peer endpoint).</summary>
    public async Task<(RemoteDelivered? Delivered, string? Error)> MessageAsync(
        RemoteMachine machine,
        string token,
        string sessionId,
        RemotePeerSender from,
        string text,
        CancellationToken ct)
    {
        var body = new RemotePeerMessage(from.MachineId, from.MachineName, from.SessionId, from.Title, text);
        var (answer, away, refused) = await RemoteMachineRequests.SendAsync(
            httpClients,
            RemoteMachineService.HttpClientName,
            machine,
            token,
            HttpMethod.Post,
            $"/api/machine/peer/sessions/{Uri.EscapeDataString(sessionId)}/message",
            body,
            RemoteSessionsJsonContext.Default.RemotePeerMessage,
            ct);
        if (answer is null)
            return (null, away ?? refused);

        using (answer)
            return (new RemoteDelivered(Text(answer.RootElement, "title") ?? sessionId, Text(answer.RootElement, "messageId")), null);
    }

    /// <summary>A page of a session on the machine, as <c>fleet_session_read</c> shows one (its peer endpoint).</summary>
    public async Task<(RemotePage? Page, string? Error)> PageAsync(
        RemoteMachine machine,
        string token,
        string sessionId,
        string? before,
        int? limit,
        CancellationToken ct)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(before))
            query.Add($"before={Uri.EscapeDataString(before.Trim())}");
        if (limit is { } count)
            query.Add($"limit={count}");
        var path = $"/api/machine/peer/sessions/{Uri.EscapeDataString(sessionId)}/page{(query.Count == 0 ? "" : "?" + string.Join('&', query))}";

        var (answer, away, refused) = await GetAsync(machine, token, path, ct);
        if (answer is null)
            return (null, away ?? refused);

        using (answer)
        {
            var title = Text(answer.RootElement, "title");
            var text = Text(answer.RootElement, "text");
            return title is null || text is null
                ? (null, $"{machine.Name} sent something that isn't Fleet's answer.")
                : (new RemotePage(title, text), null);
        }
    }

    private Task<(JsonDocument? Answer, string? Away, string? Refused)> GetAsync(RemoteMachine machine, string token, string path, CancellationToken ct)
        => RemoteMachineRequests.SendAsync<object>(httpClients, RemoteMachineService.HttpClientName, machine, token, HttpMethod.Get, path, null, null, ct);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>What a machine said about itself, or why it couldn't be asked (<paramref name="Away"/>).</summary>
/// <param name="PeerMessages">It takes messages from sessions on other machines; a Fleet before that can't take hand-offs.</param>
/// <param name="Harnesses">The harnesses a new session there can use (installed, working and on).</param>
/// <param name="Folders">The repositories it found in its folders.</param>
public sealed record RemoteMachineView(string? Away, bool PeerMessages, IReadOnlyList<string> Harnesses, IReadOnlyList<string> Folders);

/// <summary>A session here, on this machine, that messages one on another machine.</summary>
public sealed record RemotePeerSender(string MachineId, string MachineName, string SessionId, string Title);

public sealed record RemoteStarted(string SessionId, string Title, string? Branch);

public sealed record RemoteDelivered(string Title, string? MessageId);

public sealed record RemotePage(string Title, string Text);

internal sealed record RemotePeerMessage(string FromMachineId, string FromMachineName, string FromSessionId, string FromTitle, string Text);

[JsonSerializable(typeof(RemotePeerMessage))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class RemoteSessionsJsonContext : JsonSerializerContext;
