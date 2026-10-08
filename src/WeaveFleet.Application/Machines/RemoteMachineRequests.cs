using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// A request from this Fleet to another machine in its list, with the token it keeps for that machine, and what came of
/// it in words the user can act on. Automations that run there (<see cref="RemoteAutomationRuns"/>) and agents that hand
/// work there (<see cref="RemoteSessions"/>) both send through it.
/// </summary>
internal static class RemoteMachineRequests
{
    /// <summary>
    /// Sends a request to the machine. The answer's JSON; or <c>Away</c>, why it can't be asked (it didn't answer, or
    /// turned the token away); or <c>Refused</c>, what it said was wrong with the request.
    /// </summary>
    public static async Task<(JsonDocument? Answer, string? Away, string? Refused)> SendAsync<T>(
        IHttpClientFactory httpClients,
        string clientName,
        RemoteMachine machine,
        string token,
        HttpMethod method,
        string path,
        T? body,
        JsonTypeInfo<T>? bodyType,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"{machine.BaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null && bodyType is not null)
            request.Content = JsonContent.Create(body, bodyType);

        HttpResponseMessage response;
        try
        {
            response = await httpClients.CreateClient(clientName).SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            return (null, $"{machine.Name} didn't answer.", null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // It took the request but didn't finish in time: it may have done it there.
            return (null, $"{machine.Name} didn't answer in time. Look there before trying again.", null);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (null, $"{machine.Name} turned this Fleet's token away. Update it in Settings › Machines.", null);
            if (!response.IsSuccessStatusCode)
                return (null, null, await ErrorOfAsync(response, machine, ct));

            try
            {
                return (await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct), null, null);
            }
            catch (JsonException)
            {
                return (null, null, $"{machine.Name} sent something that isn't Fleet's answer.");
            }
        }
    }

    /// <summary>What the machine said went wrong (<c>{"error": "…"}</c>), or its status code.</summary>
    private static async Task<string> ErrorOfAsync(HttpResponseMessage response, RemoteMachine machine, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var name in (string[])["error", "detail", "message"])
            {
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                    return $"{machine.Name}: {text}";
            }
        }
        catch (JsonException)
        {
        }

        return $"{machine.Name} answered {(int)response.StatusCode}.";
    }
}
