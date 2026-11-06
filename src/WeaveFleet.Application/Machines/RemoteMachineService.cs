using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// The machines this Fleet knows, kept server-side so a phone's home machine can watch them all and get the phone
/// its own token on each. Adding one asks it who it is with the token given (<c>GET /api/machine</c>), the same
/// checks the web app made: it answers, speaks contract 1, uses tokens, and isn't this machine.
/// <para>
/// Fleet makes these requests itself, to addresses the owner typed: http(s) only, 5-second timeout, no redirects.
/// Only the owner can add or change machines, so a paired device can't point Fleet anywhere.
/// </para>
/// </summary>
public sealed class RemoteMachineService(
    IRemoteMachineRepository machines,
    IHttpClientFactory httpClients,
    IDataProtectionProvider dataProtection,
    MachineIdentityStore identity,
    TimeProvider time)
{
    public const string HttpClientName = "RemoteMachines";

    /// <summary>The machine contract this Fleet speaks (<c>apiVersion</c> in <c>GET /api/machine</c>).</summary>
    public const int SupportedApiVersion = 1;

    private readonly IDataProtector _tokens = dataProtection.CreateProtector("MachineTokens");

    // Why the watcher last couldn't reach each machine. In memory only: it says nothing once Fleet restarts and asks again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MachineDiagnosis> _lastDiagnoses = new(StringComparer.Ordinal);

    /// <summary>Raised after the list changes (added, changed, removed), with the machine's id.</summary>
    public event Action<string>? Changed;

    public Task<IReadOnlyList<RemoteMachine>> ListAsync() => machines.ListAsync();

    public Task<RemoteMachine?> GetAsync(string id) => machines.GetAsync(id);

    /// <summary>The machine's access token in the clear, for talking to it.</summary>
    public string TokenOf(RemoteMachine machine) => _tokens.Unprotect(machine.EncryptedToken);

    /// <summary>
    /// <see cref="TokenOf"/>, or null when it can't be read (the Data Protection keys changed since it was saved). The
    /// owner fixes that by entering the token again in Settings › Machines.
    /// </summary>
    public string? TryTokenOf(RemoteMachine machine)
    {
        try
        {
            return TokenOf(machine);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Checks the address and token against the machine and saves it. Error is what to tell the user.</summary>
    public async Task<(RemoteMachine? Machine, MachineProblem? Error)> AddAsync(string baseUrl, string token, CancellationToken cancellationToken, string? sshDestination = null)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        if (normalized is null)
            return (null, "That isn't an address. Try something like https://falcon.tail9c2e.ts.net.");
        if (string.IsNullOrWhiteSpace(token))
            return (null, "Paste the machine's access token.");

        var (info, error) = await IdentifyAsync(normalized, token.Trim(), cancellationToken);
        if (info is null)
            return (null, error);
        if (info.Id == identity.Get().Id)
            return (null, "That's this machine.");

        var existing = await machines.GetAsync(info.Id);
        var machine = new RemoteMachine
        {
            Id = info.Id,
            Name = info.Name,
            BaseUrl = normalized,
            EncryptedToken = _tokens.Protect(token.Trim()),
            Os = info.Os,
            AddedAt = existing?.AddedAt ?? time.GetUtcNow(),
            LastSeenAt = time.GetUtcNow(),
            Status = RemoteMachineStatuses.Online,
            AgentsAllowed = existing?.AgentsAllowed ?? false,
            SshDestination = sshDestination,
        };
        await machines.UpsertAsync(machine);
        Changed?.Invoke(machine.Id);
        return (machine, null);
    }

    /// <summary>
    /// Adds a machine with a one-time code from <c>fleet node pair</c> on it, so no token is typed or moved. Asks who
    /// answers first (the code isn't used up), refuses this machine, then redeems the code for a token of its own
    /// that the machine lists and can remove. The master token stays where it is.
    /// </summary>
    public async Task<(RemoteMachine? Machine, MachineProblem? Error)> AddByCodeAsync(string baseUrl, string code, CancellationToken cancellationToken, string? sshDestination = null)
    {
        var (found, previewError) = await PreviewByCodeAsync(baseUrl, code, cancellationToken);
        if (found is null)
            return (null, previewError);
        var normalized = found.BaseUrl;

        var (redeemed, redeemError) = await PostPairingAsync(
            normalized,
            "redeem",
            new Dictionary<string, string>
            {
                ["manualCode"] = code,
                ["deviceName"] = identity.Get().Name ?? Environment.MachineName,
                ["platform"] = "other",
                ["kind"] = DeviceKinds.Machine,
            },
            cancellationToken);
        if (redeemed is null)
            return (null, redeemError);

        var token = redeemed.Value.TryGetProperty("token", out var tokenElement) ? tokenElement.GetString() : null;
        if (string.IsNullOrEmpty(token))
            return (null, MachineDiagnoses.NotAFleet(normalized));
        return await AddAsync(normalized, token, cancellationToken, sshDestination);
    }

    /// <summary>
    /// Looks at the machine a code belongs to without using the code up: who it is and which Fleet it runs, so Add a
    /// machine can say what it found before adding it. Refuses this machine.
    /// </summary>
    public async Task<(PairingPreview? Found, MachineProblem? Error)> PreviewByCodeAsync(string baseUrl, string code, CancellationToken cancellationToken)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        if (normalized is null)
            return (null, "That isn't an address. Try something like http://100.64.90.72:2113.");
        if (PairingCodeStore.NormalizeManualCode(code) is null)
            return (null, "That isn't a pairing code. It looks like ABCD-EFGH; run fleet node pair on the other machine.");

        var (preview, error) = await PostPairingAsync(normalized, "preview", new Dictionary<string, string> { ["manualCode"] = code }, cancellationToken);
        if (preview is null)
            return (null, error);

        var element = preview.Value;
        var machineId = element.TryGetProperty("machineId", out var idElement) ? idElement.GetString() : null;
        if (machineId == identity.Get().Id)
            return (null, "That's this machine.");

        return (new PairingPreview(
            normalized,
            machineId ?? "",
            element.TryGetProperty("machineName", out var name) ? name.GetString() ?? normalized : normalized,
            element.TryGetProperty("os", out var os) ? os.GetString() : null,
            element.TryGetProperty("version", out var version) ? version.GetString() : null), null);
    }

    private static string ToJson(Dictionary<string, string> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values)
                writer.WriteString(key, value);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    // The pairing endpoints take no token. Fleet sends only the code, which is good for one use.
    private async Task<(JsonElement? Body, MachineProblem? Error)> PostPairingAsync(string baseUrl, string action, Dictionary<string, string> body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/pairing/{action}")
        {
            Content = new StringContent(ToJson(body), System.Text.Encoding.UTF8, "application/json"),
        };

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            // Fleet turns an unknown code away with a JSON 404; any other 404 is a Fleet that doesn't have the route.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, response.Content.Headers.ContentType?.MediaType == "application/json"
                    ? MachineDiagnoses.CodeExpiredOn(baseUrl)
                    : MachineDiagnoses.PairingLinksUnsupportedOn(baseUrl));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return (null, MachineDiagnoses.TooManyCodesOn());
            if (!response.IsSuccessStatusCode)
                return (null, MachineDiagnoses.FromStatus(response.StatusCode, baseUrl));

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return (document.RootElement.Clone(), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, MachineDiagnoses.FromException(ex, baseUrl));
        }
        catch (JsonException)
        {
            return (null, MachineDiagnoses.NotAFleet(baseUrl));
        }
    }

    /// <summary>
    /// Changes a machine's address, token, name, or whether agents may hand work to it. A new address or token is checked
    /// against the machine first.
    /// </summary>
    public async Task<(RemoteMachine? Machine, MachineProblem? Error)> UpdateAsync(
        string id,
        string? baseUrl,
        string? token,
        string? name,
        CancellationToken cancellationToken,
        bool? agentsAllowed = null)
    {
        var current = await machines.GetAsync(id);
        if (current is null)
            return (null, "No such machine.");

        var next = current;
        if (baseUrl is not null || token is not null)
        {
            var normalized = baseUrl is null ? current.BaseUrl : NormalizeBaseUrl(baseUrl);
            if (normalized is null)
                return (null, "That isn't an address.");
            // The saved token belongs to the old address. Sending it to a different one would hand it to whoever is there.
            if (string.IsNullOrWhiteSpace(token) && !string.Equals(normalized, current.BaseUrl, StringComparison.OrdinalIgnoreCase))
                return (null, "Give the machine's token again when you change its address. The saved one is only sent to the address it was made for.");
            var plainToken = string.IsNullOrWhiteSpace(token) ? TokenOf(current) : token.Trim();
            var (info, error) = await IdentifyAsync(normalized, plainToken, cancellationToken, current.Name);
            if (info is null)
                return (null, error);
            if (info.Id != id)
                return (null, $"That address is {info.Name}, not {current.Name}.");

            next = next with
            {
                BaseUrl = normalized,
                EncryptedToken = _tokens.Protect(plainToken),
                Name = info.Name,
                Os = info.Os,
                Status = RemoteMachineStatuses.Online,
                LastSeenAt = time.GetUtcNow(),
                // A new address isn't the tunnel's any more.
                SshDestination = string.Equals(normalized, current.BaseUrl, StringComparison.OrdinalIgnoreCase) ? current.SshDestination : null,
            };
        }

        if (!string.IsNullOrWhiteSpace(name))
            next = next with { Name = name.Trim() };
        if (agentsAllowed is { } allowed)
            next = next with { AgentsAllowed = allowed };

        await machines.UpsertAsync(next);
        Changed?.Invoke(id);
        return (next, null);
    }

    /// <summary>
    /// Saves machines a browser had in its own list, as they are: no request to each (some may be off). Idempotent by
    /// id; this machine and bad addresses are skipped.
    /// </summary>
    public async Task<IReadOnlyList<RemoteMachine>> ImportAsync(IEnumerable<ImportedMachine> imported)
    {
        var self = identity.Get().Id;
        foreach (var entry in imported)
        {
            var normalized = NormalizeBaseUrl(entry.BaseUrl);
            if (string.IsNullOrWhiteSpace(entry.Id) || entry.Id == self || normalized is null || string.IsNullOrWhiteSpace(entry.Token))
                continue;

            var existing = await machines.GetAsync(entry.Id);
            await machines.UpsertAsync(new RemoteMachine
            {
                Id = entry.Id,
                Name = string.IsNullOrWhiteSpace(entry.Name) ? normalized : entry.Name.Trim(),
                BaseUrl = normalized,
                EncryptedToken = _tokens.Protect(entry.Token.Trim()),
                Os = entry.Os,
                AddedAt = existing?.AddedAt ?? entry.AddedAt ?? time.GetUtcNow(),
                Status = existing?.Status ?? RemoteMachineStatuses.Unknown,
                AgentsAllowed = existing?.AgentsAllowed ?? false,
                SshDestination = existing?.SshDestination,
            });
            Changed?.Invoke(entry.Id);
        }

        return await machines.ListAsync();
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var deleted = await machines.DeleteAsync(id);
        if (deleted)
        {
            _lastDiagnoses.TryRemove(id, out _);
            Changed?.Invoke(id);
        }
        return deleted;
    }

    /// <summary>Points a machine at a new address (its SSH tunnel got another local port). Nothing else changes.</summary>
    public async Task MoveAsync(string id, string baseUrl)
    {
        if (await machines.GetAsync(id) is { } current)
        {
            await machines.UpsertAsync(current with { BaseUrl = baseUrl });
            Changed?.Invoke(id);
        }
    }

    /// <summary>Tells listeners (the watcher, the client's poll) that something about a machine changed, e.g. its tunnel.</summary>
    public void NotifyChanged(string id) => Changed?.Invoke(id);

    /// <summary>
    /// Asks a saved machine who it is, with its saved token, and says which step broke when it can't. Null when it
    /// answers. Writes nothing: the watcher owns the machine's status.
    /// </summary>
    public async Task<(bool Found, MachineProblem? Problem)> CheckAsync(string id, CancellationToken cancellationToken)
    {
        var machine = await machines.GetAsync(id);
        if (machine is null)
            return (false, null);
        if (TryTokenOf(machine) is not { } token)
            return (true, MachineDiagnoses.KeyUnreadable(machine.BaseUrl, machine.Name));

        var (info, problem) = await IdentifyAsync(machine.BaseUrl, token, cancellationToken, machine.Name);
        if (info is null)
            return (true, problem);
        return (true, info.Id == id ? null : new MachineProblem($"That address is {info.Name}, not {machine.Name}."));
    }

    /// <summary>Records what the watcher saw of a machine, and why when it couldn't reach it.</summary>
    public Task SetStatusAsync(string id, string status, bool seen, MachineDiagnosis? diagnosis = null)
    {
        if (diagnosis is null)
            _lastDiagnoses.TryRemove(id, out _);
        else
            _lastDiagnoses[id] = diagnosis;
        return machines.UpdateStatusAsync(id, status, seen ? time.GetUtcNow() : null);
    }

    /// <summary>Why the watcher last couldn't reach a machine; null while it answers, or before the watcher has tried.</summary>
    public MachineDiagnosis? LastDiagnosisOf(string id) => _lastDiagnoses.GetValueOrDefault(id);

    /// <summary>
    /// Asks <paramref name="baseUrl"/> who it is with <paramref name="token"/>. <paramref name="machineName"/> is what a problem
    /// calls the machine, when it is already known; the address' host otherwise.
    /// </summary>
    public async Task<(RemoteMachineInfo? Info, MachineProblem? Error)> IdentifyAsync(string baseUrl, string token, CancellationToken cancellationToken, string? machineName = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/machine");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, MachineDiagnoses.FromException(ex, baseUrl, machineName));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 400)
                return (null, MachineDiagnoses.FromStatus(response.StatusCode, baseUrl, machineName));

            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var root = document.RootElement;
                var apiVersion = root.TryGetProperty("apiVersion", out var v) && v.TryGetInt32(out var version) ? version : 0;
                if (apiVersion != SupportedApiVersion)
                    return (null, MachineDiagnoses.VersionMismatchOf(apiVersion, SupportedApiVersion, baseUrl, machineName));
                if (!root.TryGetProperty("authMode", out var mode) || mode.GetString() != "token")
                    return (null, MachineDiagnoses.SignInUnsupported(baseUrl, machineName));
                var id = root.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id))
                    return (null, MachineDiagnoses.NotAFleet(baseUrl, machineName));

                return (new RemoteMachineInfo(
                    id,
                    root.TryGetProperty("name", out var name) ? name.GetString() ?? id : id,
                    root.TryGetProperty("os", out var os) ? os.GetString() : null), null);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return (null, MachineDiagnoses.NotAFleet(baseUrl, machineName));
            }
        }
    }

    /// <summary>An absolute http(s) URL as scheme, host, port and path, without a trailing slash; null otherwise.</summary>
    public static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = $"http://{trimmed}";
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}

/// <summary>Who a machine says it is.</summary>
public sealed record RemoteMachineInfo(string Id, string Name, string? Os);

/// <summary>A machine from a browser's own list (<c>weave:machines</c>), as it kept it.</summary>
public sealed record ImportedMachine(string Id, string? Name, string BaseUrl, string Token, string? Os, DateTimeOffset? AddedAt);

/// <summary>What a machine said about itself when asked about a pairing code. <c>Version</c> is null for an older Fleet.</summary>
public sealed record PairingPreview(string BaseUrl, string MachineId, string Name, string? Os, string? Version);
