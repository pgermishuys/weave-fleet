using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// The parts of OpenCode 2's server API (<c>/api/…</c>) Fleet uses. One per server; the caller sets the base
/// address and Basic auth on both clients.
/// </summary>
/// <param name="http">For requests; has a timeout.</param>
/// <param name="events">For the event stream; no timeout, since the stream stays open.</param>
internal sealed partial class OpenCode2HttpClient(HttpClient http, HttpClient events, ILogger<OpenCode2HttpClient> logger) : IDisposable
{
    /// <summary>
    /// Every V2 session Fleet creates allows everything, the way Fleet runs OpenCode (1.x) headless: nobody is
    /// there to answer a permission prompt. The session's rules win over an <c>ask</c> in the user's config.
    /// </summary>
    internal static readonly IReadOnlyList<OpenCode2PermissionRule> AllowAll =
        [new OpenCode2PermissionRule { Action = "*", Resource = "*", Effect = "allow" }];

    /// <summary>
    /// Hides the workflow step tool. V2 applies the last rule that matches, and drops a tool that's denied for every
    /// resource from what the model is offered, so the deny goes after <see cref="AllowAll"/>.
    /// </summary>
    internal static readonly OpenCode2PermissionRule DenyStepTool =
        new() { Action = WeaveFleet.Application.Workflows.FleetWorkflows.StepTool, Resource = "*", Effect = "deny" };

    /// <summary><see cref="AllowAll"/>, then <see cref="DenyStepTool"/>: every session on a server with workflows on that isn't a step.</summary>
    internal static readonly IReadOnlyList<OpenCode2PermissionRule> AllowAllButStepTool = [.. AllowAll, DenyStepTool];

    public Task<OpenCode2ServerInfo?> GetInfoAsync(CancellationToken ct)
        => http.GetFromJsonAsync("api/info", OpenCode2JsonContext.Default.OpenCode2ServerInfo, ct);

    /// <param name="hideStepTool">The server has the workflow step tool and the session isn't a step.</param>
    public async Task<OpenCode2SessionInfo> CreateSessionAsync(string directory, CancellationToken ct, bool hideStepTool = false)
    {
        using var response = await http.PostAsJsonAsync(
            "api/session",
            new OpenCode2CreateSessionRequest
            {
                Location = new OpenCode2Location { Directory = directory },
                Permissions = hideStepTool ? AllowAllButStepTool : AllowAll,
            },
            OpenCode2JsonContext.Default.OpenCode2CreateSessionRequest,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "create a session", ct).ConfigureAwait(false);

        var body = await response.Content.ReadFromJsonAsync(
            OpenCode2JsonContext.Default.OpenCode2EnvelopeOpenCode2SessionInfo, ct).ConfigureAwait(false);
        return body?.Data is { Id: { Length: > 0 } } session
            ? session
            : throw new InvalidOperationException("OpenCode 2 created a session but didn't say its id.");
    }

    /// <summary>The session, or <see langword="null"/> when this server's database has no session <paramref name="sessionId"/>.</summary>
    public async Task<OpenCode2SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/session/{Uri.EscapeDataString(sessionId)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "read the session", ct).ConfigureAwait(false);

        var body = await response.Content.ReadFromJsonAsync(
            OpenCode2JsonContext.Default.OpenCode2EnvelopeOpenCode2SessionInfo, ct).ConfigureAwait(false);
        return body?.Data;
    }

    /// <summary>Replaces the session's permission rules.</summary>
    public async Task SetPermissionsAsync(string sessionId, IReadOnlyList<OpenCode2PermissionRule> permissions, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/session/{Uri.EscapeDataString(sessionId)}")
        {
            Content = JsonContent.Create(
                new OpenCode2SessionUpdateRequest { Permissions = permissions },
                OpenCode2JsonContext.Default.OpenCode2SessionUpdateRequest),
        };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "change the session's permissions", ct).ConfigureAwait(false);
    }

    public async Task PromptAsync(
        string sessionId,
        string text,
        string? messageId,
        IReadOnlyList<OpenCode2PromptFile>? files,
        CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/prompt",
            new OpenCode2PromptRequest { Id = messageId, Text = text, Files = files is { Count: > 0 } ? files : null },
            OpenCode2JsonContext.Default.OpenCode2PromptRequest,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "send the prompt", ct).ConfigureAwait(false);
    }

    public async Task InterruptAsync(string sessionId, CancellationToken ct)
    {
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/interrupt", content, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "interrupt the session", ct).ConfigureAwait(false);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        using var response = await http.DeleteAsync($"api/session/{Uri.EscapeDataString(sessionId)}", ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, "delete the session", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One page of the session's messages, newest first. <paramref name="cursor"/> is a page's <c>cursor.next</c>,
    /// for the page of older messages after it.
    /// </summary>
    public async Task<OpenCode2MessagePage> GetMessagesAsync(string sessionId, int? limit, string? cursor, CancellationToken ct)
    {
        var query = new List<string>();
        if (limit is { } n)
            query.Add($"limit={n}");
        // V2 takes either an order or a cursor; newest first is its order for a first page.
        query.Add(cursor is null ? "order=desc" : $"cursor={Uri.EscapeDataString(cursor)}");

        using var response = await http.GetAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/message?{string.Join('&', query)}", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "read the session's messages", ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(OpenCode2JsonContext.Default.OpenCode2MessagePage, ct).ConfigureAwait(false)
            ?? new OpenCode2MessagePage();
    }

    /// <summary>The session's forms waiting for an answer (the question tool asks with a form).</summary>
    public async Task<IReadOnlyList<OpenCode2Form>> GetFormsAsync(string sessionId, CancellationToken ct)
    {
        var body = await http.GetFromJsonAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/form",
            OpenCode2JsonContext.Default.OpenCode2EnvelopeListOpenCode2Form,
            ct).ConfigureAwait(false);
        return body?.Data ?? [];
    }

    public async Task ReplyToFormAsync(string sessionId, string formId, Dictionary<string, JsonElement> answer, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/form/{Uri.EscapeDataString(formId)}/reply",
            new OpenCode2FormReply { Answer = answer },
            OpenCode2JsonContext.Default.OpenCode2FormReply,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "answer the question", ct).ConfigureAwait(false);
    }

    /// <summary>Dismisses a form. V2 fails the tool call that asked, which ends the turn.</summary>
    public async Task CancelFormAsync(string sessionId, string formId, CancellationToken ct)
    {
        using var response = await http.DeleteAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/form/{Uri.EscapeDataString(formId)}", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "dismiss the question", ct).ConfigureAwait(false);
    }

    /// <summary>Answers a permission request: <paramref name="decision"/> is <c>once</c>, <c>always</c> or <c>reject</c>.</summary>
    public async Task ReplyToPermissionAsync(string sessionId, string requestId, string decision, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/permission/{Uri.EscapeDataString(requestId)}/reply",
            new OpenCode2PermissionReply { Decision = decision },
            OpenCode2JsonContext.Default.OpenCode2PermissionReply,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "answer the permission request", ct).ConfigureAwait(false);
    }

    /// <summary>Selects the agent the session's next turns run as. V2 takes any name here and fails the turn if it's unknown.</summary>
    public async Task SwitchAgentAsync(string sessionId, string agent, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/agent",
            new OpenCode2SwitchAgentRequest { Agent = agent },
            OpenCode2JsonContext.Default.OpenCode2SwitchAgentRequest,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "switch the agent", ct).ConfigureAwait(false);
    }

    /// <summary>Selects the model (and variant) the session's next turns use.</summary>
    public async Task SwitchModelAsync(string sessionId, OpenCode2ModelRef model, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/model",
            new OpenCode2SwitchModelRequest { Model = model },
            OpenCode2JsonContext.Default.OpenCode2SwitchModelRequest,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "switch the model", ct).ConfigureAwait(false);
    }

    /// <summary>Runs a command as the session's next turn; V2 sends its expanded template as the user's message.</summary>
    public async Task RunCommandAsync(string sessionId, string name, string arguments, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/command",
            new OpenCode2CommandRequest { Name = name, Text = arguments },
            OpenCode2JsonContext.Default.OpenCode2CommandRequest,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "run the command", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a command the user typed in the session's folder. V2 keeps it as a <c>shell</c> message under
    /// <paramref name="messageId"/>, streams <c>session.shell.started</c> and <c>session.shell.ended</c>, and gives the
    /// output to the model with the next prompt.
    /// </summary>
    public async Task RunShellAsync(string sessionId, string? messageId, string command, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/shell",
            new OpenCode2ShellRequest { Id = messageId, Command = command },
            OpenCode2JsonContext.Default.OpenCode2ShellRequest,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "run the command", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers <paramref name="prompt"/> from the session's conversation without adding to it. It waits for the whole
    /// answer, so it has no request timeout of its own; <paramref name="ct"/> bounds it.
    /// </summary>
    public async Task<string?> GenerateAsync(string sessionId, string prompt, CancellationToken ct)
    {
        using var response = await events.PostAsJsonAsync(
            $"api/session/{Uri.EscapeDataString(sessionId)}/generate",
            new OpenCode2GenerateRequest { Prompt = prompt },
            OpenCode2JsonContext.Default.OpenCode2GenerateRequest,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "answer off the record", ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync(
            OpenCode2JsonContext.Default.OpenCode2EnvelopeOpenCode2GenerateResult, ct).ConfigureAwait(false);
        return body?.Data?.Text;
    }

    /// <summary>
    /// Loads <paramref name="directory"/> on the server. V2 reads a folder's config lazily: until something loads it,
    /// its catalog comes back without the folder's (and the user's) agents, models and commands.
    /// </summary>
    public async Task LoadLocationAsync(string directory, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/location?{LocationQuery(directory)}", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "load the folder", ct).ConfigureAwait(false);
    }

    /// <summary>The agents in <paramref name="directory"/>; load the location first.</summary>
    public async Task<IReadOnlyList<OpenCode2AgentInfo>> GetAgentsAsync(string directory, CancellationToken ct)
        => (await http.GetFromJsonAsync(
            $"api/agent?{LocationQuery(directory)}",
            OpenCode2JsonContext.Default.OpenCode2EnvelopeListOpenCode2AgentInfo,
            ct).ConfigureAwait(false))?.Data ?? [];

    /// <summary>The models in <paramref name="directory"/>, of every provider; load the location first.</summary>
    public async Task<IReadOnlyList<OpenCode2ModelInfo>> GetModelsAsync(string directory, CancellationToken ct)
        => (await http.GetFromJsonAsync(
            $"api/model?{LocationQuery(directory)}",
            OpenCode2JsonContext.Default.OpenCode2EnvelopeListOpenCode2ModelInfo,
            ct).ConfigureAwait(false))?.Data ?? [];

    /// <summary>The model a session gets when nothing picks one (the config's <c>model</c>, else V2's choice), if any.</summary>
    public async Task<OpenCode2ModelInfo?> GetDefaultModelAsync(string directory, CancellationToken ct)
        => (await http.GetFromJsonAsync(
            $"api/model/default?{LocationQuery(directory)}",
            OpenCode2JsonContext.Default.OpenCode2EnvelopeOpenCode2ModelInfo,
            ct).ConfigureAwait(false))?.Data;

    public async Task<IReadOnlyList<OpenCode2ProviderInfo>> GetProvidersAsync(string directory, CancellationToken ct)
        => (await http.GetFromJsonAsync(
            $"api/provider?{LocationQuery(directory)}",
            OpenCode2JsonContext.Default.OpenCode2EnvelopeListOpenCode2ProviderInfo,
            ct).ConfigureAwait(false))?.Data ?? [];

    public async Task<IReadOnlyList<OpenCode2CommandInfo>> GetCommandsAsync(string directory, CancellationToken ct)
        => (await http.GetFromJsonAsync(
            $"api/command?{LocationQuery(directory)}",
            OpenCode2JsonContext.Default.OpenCode2EnvelopeListOpenCode2CommandInfo,
            ct).ConfigureAwait(false))?.Data ?? [];

    /// <summary>The config V2 read for <paramref name="directory"/>, global first and the folder's own last.</summary>
    public async Task<IReadOnlyList<OpenCode2ConfigSource>> GetConfigAsync(string directory, CancellationToken ct)
        => await http.GetFromJsonAsync(
            $"api/config?{LocationQuery(directory)}",
            OpenCode2JsonContext.Default.ListOpenCode2ConfigSource,
            ct).ConfigureAwait(false) ?? [];

    /// <summary>The ids of the sessions with a turn running on this server.</summary>
    public async Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(CancellationToken ct)
    {
        var body = await http.GetFromJsonAsync(
            "api/session/active",
            OpenCode2JsonContext.Default.OpenCode2EnvelopeDictionaryStringJsonElement,
            ct).ConfigureAwait(false);
        return body?.Data is { } active
            ? active.Keys.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The folders the server has loaded (<c>GET /api/debug/location</c>). V2 loads a folder on first use (a turn, a
    /// catalog read), and only a loaded folder can have a shell running.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetLoadedLocationsAsync(CancellationToken ct)
        => (await http.GetFromJsonAsync(
            "api/debug/location",
            OpenCode2JsonContext.Default.ListOpenCode2Location,
            ct).ConfigureAwait(false))?.Select(l => l.Directory).ToList() ?? [];

    /// <summary>
    /// The shell commands running in <paramref name="directory"/>, including backgrounded shell calls whose turn has
    /// ended. V2 lists one folder at a time (without one, only the folder it was started in), and asking about a
    /// folder loads it.
    /// </summary>
    public async Task<IReadOnlyList<OpenCode2ShellInfo>> GetRunningShellsAsync(string directory, CancellationToken ct)
        => (await http.GetFromJsonAsync(
            $"api/shell?{LocationQuery(directory)}",
            OpenCode2JsonContext.Default.OpenCode2EnvelopeListOpenCode2ShellInfo,
            ct).ConfigureAwait(false))?.Data ?? [];

    /// <summary>
    /// Every provider V2 can sign in to in <paramref name="directory"/>, with its sign-in methods and the sign-ins it
    /// has. Integrations, and the browser sign-ins under way, belong to a location; load it first.
    /// </summary>
    public async Task<IReadOnlyList<OpenCode2Integration>> GetIntegrationsAsync(string directory, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/integration?{LocationQuery(directory)}", ct).ConfigureAwait(false);
        await EnsureSignInSuccessAsync(response, "list its providers", ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync(
            OpenCode2JsonContext.Default.OpenCode2EnvelopeListOpenCode2Integration, ct).ConfigureAwait(false);
        return body?.Data ?? [];
    }

    /// <summary>Stores a key for <paramref name="integrationId"/> and makes it the one in use. The key is only ever in the request body.</summary>
    public async Task ConnectKeyAsync(string directory, string integrationId, string key, IReadOnlyDictionary<string, JsonElement> answer, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/integration/{Uri.EscapeDataString(integrationId)}/connect/key?{LocationQuery(directory)}",
            new OpenCode2ConnectKeyRequest { Key = key, Answer = answer.Count > 0 ? answer : null },
            OpenCode2JsonContext.Default.OpenCode2ConnectKeyRequest,
            ct).ConfigureAwait(false);
        await EnsureSignInSuccessAsync(response, "sign in", ct).ConfigureAwait(false);
    }

    /// <summary>Starts a browser sign-in; V2 keeps the attempt in this server's memory for ten minutes.</summary>
    public async Task<OpenCode2OAuthAttempt> StartOAuthAsync(
        string directory,
        string integrationId,
        string methodId,
        IReadOnlyDictionary<string, JsonElement> answer,
        CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/integration/{Uri.EscapeDataString(integrationId)}/connect/oauth?{LocationQuery(directory)}",
            new OpenCode2StartOAuthRequest { MethodId = methodId, Answer = answer.Count > 0 ? answer : null },
            OpenCode2JsonContext.Default.OpenCode2StartOAuthRequest,
            ct).ConfigureAwait(false);
        await EnsureSignInSuccessAsync(response, "start the sign-in", ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync(
            OpenCode2JsonContext.Default.OpenCode2EnvelopeOpenCode2OAuthAttempt, ct).ConfigureAwait(false);
        return body?.Data is { AttemptId: { Length: > 0 }, Url: { Length: > 0 } } attempt
            ? attempt
            : throw new HarnessSignInException("OpenCode 2 started the sign-in but didn't say where to go.");
    }

    /// <summary>
    /// Where a browser sign-in is; <see langword="null"/> when V2 no longer knows it (cancelled, or finished more than
    /// a minute ago).
    /// </summary>
    public async Task<OpenCode2OAuthStatus?> GetOAuthStatusAsync(string directory, string integrationId, string attemptId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{OAuthAttemptPath(integrationId, attemptId)}?{LocationQuery(directory)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSignInSuccessAsync(response, "check the sign-in", ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync(
            OpenCode2JsonContext.Default.OpenCode2EnvelopeOpenCode2OAuthStatus, ct).ConfigureAwait(false);
        return body?.Data;
    }

    /// <summary>Finishes a browser sign-in in <c>code</c> mode with the code the provider showed.</summary>
    public async Task CompleteOAuthAsync(string directory, string integrationId, string attemptId, string code, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"{OAuthAttemptPath(integrationId, attemptId)}/complete?{LocationQuery(directory)}",
            new OpenCode2CompleteOAuthRequest { Code = code },
            OpenCode2JsonContext.Default.OpenCode2CompleteOAuthRequest,
            ct).ConfigureAwait(false);
        await EnsureSignInSuccessAsync(response, "finish the sign-in", ct).ConfigureAwait(false);
    }

    /// <summary>Stops a browser sign-in, which closes its callback listener. One V2 no longer knows is left alone.</summary>
    public async Task CancelOAuthAsync(string directory, string integrationId, string attemptId, CancellationToken ct)
    {
        using var response = await http.DeleteAsync($"{OAuthAttemptPath(integrationId, attemptId)}?{LocationQuery(directory)}", ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSignInSuccessAsync(response, "cancel the sign-in", ct).ConfigureAwait(false);
    }

    /// <summary>Makes a stored sign-in the one its provider uses.</summary>
    public async Task ActivateCredentialAsync(string credentialId, CancellationToken ct)
    {
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(
            $"api/credential/{Uri.EscapeDataString(credentialId)}/activate", content, ct).ConfigureAwait(false);
        await EnsureSignInSuccessAsync(response, "switch sign-ins", ct).ConfigureAwait(false);
    }

    /// <summary>Removes a stored sign-in; the provider's newest other one takes over.</summary>
    public async Task RemoveCredentialAsync(string credentialId, CancellationToken ct)
    {
        using var response = await http.DeleteAsync($"api/credential/{Uri.EscapeDataString(credentialId)}", ct).ConfigureAwait(false);
        await EnsureSignInSuccessAsync(response, "sign out", ct).ConfigureAwait(false);
    }

    private static string OAuthAttemptPath(string integrationId, string attemptId)
        => $"api/integration/{Uri.EscapeDataString(integrationId)}/connect/oauth/{Uri.EscapeDataString(attemptId)}";

    /// <summary>
    /// For sign-in requests, which carry keys and codes: V2's refusal (400, 404) becomes a
    /// <see cref="HarnessSignInException"/> with V2's own message, and anything else an error that says only what failed,
    /// never the response or the request.
    /// </summary>
    private static async Task EnsureSignInSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            OpenCode2ErrorBody? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync(OpenCode2JsonContext.Default.OpenCode2ErrorBody, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
            }

            if (error?.Message is { Length: > 0 } message)
                throw new HarnessSignInException(message.Length > 300 ? message[..300] : message, notFound: response.StatusCode == HttpStatusCode.NotFound);
        }

        throw new HttpRequestException(
            $"OpenCode 2 couldn't {action}: {(int)response.StatusCode} {response.ReasonPhrase}.",
            inner: null,
            response.StatusCode);
    }

    /// <summary>
    /// Reads <c>GET /api/event</c> until it ends. <paramref name="connected"/> runs once the server has accepted the
    /// subscription, before any event is read. The stream is live only: V2 doesn't replay what was missed.
    /// </summary>
    public async IAsyncEnumerable<OpenCode2Event> ReadEventsAsync(
        Action connected,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/event");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await events.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "subscribe to events", ct).ConfigureAwait(false);
        connected();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var data = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            // An event is "data:" lines up to a blank line; lines starting with ':' are heartbeats.
            if (line.Length == 0)
            {
                if (data.Length > 0 && Parse(data.ToString()) is { } evt)
                    yield return evt;
                data.Clear();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart(' '));
            }
        }
    }

    /// <summary>One event's JSON, or <see langword="null"/> when it isn't an event Fleet can read.</summary>
    internal OpenCode2Event? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, OpenCode2JsonContext.Default.OpenCode2Event);
        }
        catch (JsonException ex)
        {
            LogUnreadableEvent(logger, ex);
            return null;
        }
    }

    /// <summary>V2 takes a location as a deep-object query parameter: <c>location[directory]=…</c>.</summary>
    internal static string LocationQuery(string directory) => $"location%5Bdirectory%5D={Uri.EscapeDataString(directory)}";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (body.Length > 500)
            body = body[..500];
        throw new HttpRequestException(
            $"OpenCode 2 couldn't {action}: {(int)response.StatusCode} {response.ReasonPhrase}. {body}".TrimEnd(),
            inner: null,
            response.StatusCode);
    }

    public void Dispose()
    {
        http.Dispose();
        events.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped an OpenCode 2 event Fleet couldn't read")]
    private static partial void LogUnreadableEvent(ILogger logger, Exception exception);
}
