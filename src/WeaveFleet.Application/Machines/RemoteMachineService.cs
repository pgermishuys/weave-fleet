using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using WeaveFleet.Application.Configuration;
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
    public async Task<(RemoteMachine? Machine, string? Error)> AddAsync(string baseUrl, string token, CancellationToken cancellationToken)
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
        };
        await machines.UpsertAsync(machine);
        Changed?.Invoke(machine.Id);
        return (machine, null);
    }

    /// <summary>
    /// Changes a machine's address, token, name, or whether agents may hand work to it. A new address or token is checked
    /// against the machine first.
    /// </summary>
    public async Task<(RemoteMachine? Machine, string? Error)> UpdateAsync(
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
            var plainToken = string.IsNullOrWhiteSpace(token) ? TokenOf(current) : token.Trim();
            var (info, error) = await IdentifyAsync(normalized, plainToken, cancellationToken);
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
            });
            Changed?.Invoke(entry.Id);
        }

        return await machines.ListAsync();
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var deleted = await machines.DeleteAsync(id);
        if (deleted)
            Changed?.Invoke(id);
        return deleted;
    }

    /// <summary>Records what the watcher saw of a machine.</summary>
    public Task SetStatusAsync(string id, string status, bool seen) =>
        machines.UpdateStatusAsync(id, status, seen ? time.GetUtcNow() : null);

    /// <summary>Asks <paramref name="baseUrl"/> who it is with <paramref name="token"/>.</summary>
    public async Task<(RemoteMachineInfo? Info, string? Error)> IdentifyAsync(string baseUrl, string token, CancellationToken cancellationToken)
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
            return (null, $"Couldn't reach {baseUrl}. Check the address, that Fleet is running there, and that this machine can reach it.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (null, "That token wasn't accepted.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, "That Fleet is too old to add as a machine. Update it first.");
            if ((int)response.StatusCode is >= 300 and < 400)
                return (null, $"{baseUrl} redirects elsewhere. Use the address it redirects to.");
            if (!response.IsSuccessStatusCode)
                return (null, $"The machine answered {(int)response.StatusCode}.");

            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var root = document.RootElement;
                var apiVersion = root.TryGetProperty("apiVersion", out var v) && v.TryGetInt32(out var version) ? version : 0;
                if (apiVersion != SupportedApiVersion)
                    return (null, $"That Fleet speaks machine contract {apiVersion}; this one speaks {SupportedApiVersion}. Update the older one.");
                if (!root.TryGetProperty("authMode", out var mode) || mode.GetString() != "token")
                    return (null, "That Fleet signs people in with an identity provider, which machines don't support yet.");
                var id = root.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id))
                    return (null, "That isn't a Fleet.");

                return (new RemoteMachineInfo(
                    id,
                    root.TryGetProperty("name", out var name) ? name.GetString() ?? id : id,
                    root.TryGetProperty("os", out var os) ? os.GetString() : null), null);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return (null, "That isn't a Fleet.");
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
