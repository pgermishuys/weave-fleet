using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Acp;

namespace WeaveFleet.Infrastructure.Harnesses.Copilot;

/// <summary>
/// GitHub Copilot's sign-in from Settings, through weave-copilot (<c>_fleet/auth/*</c>), which drives GitHub's own
/// <c>copilot login --device-code</c>. The token goes where the CLI keeps it (its <c>COPILOT_HOME</c>): Fleet only sees
/// who is signed in, never a token, and a sign-in under way is only a code and a page to show.
/// </summary>
/// <remarks>
/// The CLI's login runs inside the weave-copilot process that started it, so a sign-in under way keeps that process
/// until it finishes, is cancelled or runs out.
/// </remarks>
internal sealed class CopilotSignIn(Func<CancellationToken, Task<IAcpAgent>> start, Action changed, TimeProvider time) : IHarnessProviderSignIn
{
    internal const string ProviderId = "github-copilot";
    internal const string MethodId = "device";
    private const string ConnectionId = "github-copilot-login";

    private readonly ConcurrentDictionary<string, (IAcpAgent Agent, string Remote, DateTimeOffset Expires)> _attempts = new();

    public async Task<HarnessSignIns> ListAsync(string ownerUserId, CancellationToken ct)
    {
        var status = await CopilotAuthStatus.AskAsync(start, ct).ConfigureAwait(false);
        List<HarnessSignInConnection> connections = [];
        if (status.Authenticated)
        {
            var who = string.IsNullOrWhiteSpace(status.Login) ? "your GitHub account" : status.Login;
            connections.Add(status.AuthType is "env" or "gh-cli"
                ? new HarnessSignInConnection(HarnessSignInConnectionKinds.Environment,
                    status.AuthType == "env" ? "COPILOT_GITHUB_TOKEN" : "gh", $"{who}, through {(status.AuthType == "env" ? "an environment variable" : "the GitHub CLI")}", true)
                : new HarnessSignInConnection(HarnessSignInConnectionKinds.Credential, ConnectionId, $"Signed in as {who}", true));
        }

        var method = new HarnessSignInMethod(HarnessSignInMethodTypes.OAuth, MethodId, "Sign in with GitHub");
        return new HarnessSignIns([new HarnessSignInProvider(ProviderId, "GitHub Copilot", [method], connections)],
            "Copilot keeps its sign-in itself, in its own folder (COPILOT_HOME, ~/.copilot by default). Fleet never sees or stores it.");
    }

    public Task SignInWithKeyAsync(string ownerUserId, string providerId, string key, IReadOnlyDictionary<string, JsonElement> answers, CancellationToken ct)
        => throw new HarnessSignInException("GitHub Copilot signs in with GitHub's device code, not a key.");

    public async Task<HarnessSignInAttempt> StartAsync(string ownerUserId, string providerId, string methodId, IReadOnlyDictionary<string, JsonElement> answers, CancellationToken ct)
    {
        if (providerId != ProviderId || methodId != MethodId)
            throw new HarnessSignInException("That isn't a way to sign in to GitHub Copilot.", notFound: true);

        // One sign-in at a time: a second start replaces the first.
        foreach (var earlier in _attempts.Keys)
            await ReleaseAsync(earlier, cancel: true).ConfigureAwait(false);

        var agent = await start(ct).ConfigureAwait(false);
        try
        {
            var login = await agent.Client.RequestAsync("_fleet/auth/login", new JsonObject(), ct).ConfigureAwait(false);
            var id = AcpMapper.Str(login, "attemptId")!;
            var expires = DateTimeOffset.TryParse(AcpMapper.Str(login, "expiresAt"), out var parsed) ? parsed : time.GetUtcNow().AddMinutes(15);
            _attempts[id] = (agent, id, expires);
            return new HarnessSignInAttempt(id, AcpMapper.Str(login, "url")!, $"Open the page and enter this code: {AcpMapper.Str(login, "code")}", NeedsCode: false, expires);
        }
        catch (Exception ex) when (ex is AcpException or InvalidOperationException)
        {
            await agent.DisposeAsync().ConfigureAwait(false);
            throw new HarnessSignInException(ex.Message);
        }
    }

    public async Task<HarnessSignInAttemptStatus> GetAttemptAsync(string ownerUserId, string providerId, string attemptId, CancellationToken ct)
    {
        if (!_attempts.TryGetValue(attemptId, out var attempt))
            return new HarnessSignInAttemptStatus(HarnessSignInAttemptStates.Gone);
        if (time.GetUtcNow() > attempt.Expires)
        {
            await ReleaseAsync(attemptId, cancel: true).ConfigureAwait(false);
            return new HarnessSignInAttemptStatus(HarnessSignInAttemptStates.Expired);
        }

        JsonElement state;
        try { state = await attempt.Agent.Client.RequestAsync("_fleet/auth/attempt", new JsonObject { ["attemptId"] = attempt.Remote }, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is AcpException or IOException)
        {
            await ReleaseAsync(attemptId, cancel: false).ConfigureAwait(false);
            return new HarnessSignInAttemptStatus(HarnessSignInAttemptStates.Failed, "GitHub Copilot's sign-in stopped before it finished.");
        }

        var name = AcpMapper.Str(state, "state") ?? HarnessSignInAttemptStates.Gone;
        if (name == HarnessSignInAttemptStates.Pending)
            return new HarnessSignInAttemptStatus(name);
        await ReleaseAsync(attemptId, cancel: false).ConfigureAwait(false);
        changed();
        return new HarnessSignInAttemptStatus(name, name == HarnessSignInAttemptStates.Failed ? AcpMapper.Str(state, "message") : null);
    }

    public Task SubmitCodeAsync(string ownerUserId, string providerId, string attemptId, string code, CancellationToken ct)
        => throw new HarnessSignInException("GitHub Copilot's sign-in needs no code pasted back: enter the code on GitHub's page.");

    public Task ForwardCallbackAsync(string ownerUserId, string providerId, string attemptId, Uri landedOn, CancellationToken ct)
        => throw new HarnessSignInException("GitHub Copilot's sign-in has no callback address.");

    public async Task CancelAsync(string ownerUserId, string providerId, string attemptId, CancellationToken ct)
        => await ReleaseAsync(attemptId, cancel: true).ConfigureAwait(false);

    public Task UseAsync(string ownerUserId, string connectionId, CancellationToken ct)
        => throw new HarnessSignInException("GitHub Copilot has one sign-in at a time.");

    public async Task SignOutAsync(string ownerUserId, string connectionId, CancellationToken ct)
    {
        if (connectionId != ConnectionId)
            throw new HarnessSignInException("Fleet can only sign out the sign-in Copilot keeps itself.", notFound: true);
        await using var agent = await start(ct).ConfigureAwait(false);
        try { await agent.Client.RequestAsync("_fleet/auth/logout", new JsonObject(), ct).ConfigureAwait(false); }
        catch (AcpException ex) { throw new HarnessSignInException(ex.Message); }
        changed();
    }

    private async Task ReleaseAsync(string attemptId, bool cancel)
    {
        if (!_attempts.TryRemove(attemptId, out var attempt))
            return;
        if (cancel)
        {
            try { await attempt.Agent.Client.RequestAsync("_fleet/auth/cancel", new JsonObject { ["attemptId"] = attempt.Remote }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is AcpException or IOException or TimeoutException) { /* disposing the process ends the login anyway */ }
        }

        await attempt.Agent.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Who weave-copilot says is signed in to Copilot; no token is part of the answer.</summary>
internal sealed record CopilotAuthStatus(bool Authenticated, string? AuthType, string? Login)
{
    public static async Task<CopilotAuthStatus> AskAsync(Func<CancellationToken, Task<IAcpAgent>> start, CancellationToken ct)
    {
        await using var agent = await start(ct).ConfigureAwait(false);
        var reply = await agent.Client.RequestAsync("_fleet/auth/status", new JsonObject(), ct).ConfigureAwait(false);
        return new CopilotAuthStatus(reply.TryGetProperty("authenticated", out var yes) && yes.ValueKind == JsonValueKind.True,
            AcpMapper.Str(reply, "authType"), AcpMapper.Str(reply, "login"));
    }
}
