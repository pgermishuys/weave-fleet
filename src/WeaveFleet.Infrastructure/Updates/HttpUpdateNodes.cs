using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WeaveFleet.Application.Machines;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Infrastructure.Updates;

/// <summary><see cref="IUpdateNodes"/> over HTTP, with the token this Fleet keeps for each machine (the owner's).</summary>
public sealed class HttpUpdateNodes(RemoteMachineService machines, IHttpClientFactory httpClients) : IUpdateNodes
{
    public async Task<NodeUpdateInfo?> ReadAsync(RemoteMachine machine, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(machine, HttpMethod.Get, "/api/machine", ct);
            if (response is null || !response.IsSuccessStatusCode)
                return null;

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = body.RootElement;
            var version = Text(root, "version");
            if (version is null)
                return null;

            var update = root.TryGetProperty("update", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
            var working = root.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Object
                && c.TryGetProperty("sessions", out var s) && s.TryGetProperty("working", out var w) && w.TryGetInt32(out var n) ? n : 0;
            UpdateFailure? rolledBack = null;
            if (update.ValueKind == JsonValueKind.Object && update.TryGetProperty("rolledBack", out var r) && r.ValueKind == JsonValueKind.Object)
                rolledBack = new UpdateFailure(Text(r, "from") ?? "", Text(r, "to") ?? "", Text(r, "reason") ?? "", Text(r, "at") ?? "");

            return new NodeUpdateInfo(
                version,
                update.ValueKind == JsonValueKind.Object ? Text(update, "stagedVersion") : null,
                working,
                update.ValueKind == JsonValueKind.Object && update.TryGetProperty("canRestart", out var can) && can.ValueKind == JsonValueKind.True,
                update.ValueKind == JsonValueKind.Object ? Text(update, "restartUnavailableReason") ?? (update.TryGetProperty("canRestart", out _) ? null : "too old to restart itself") : null,
                rolledBack,
                update.ValueKind == JsonValueKind.Object ? Text(update, "rule") : null,
                !(root.TryGetProperty("webApp", out var web) && web.ValueKind == JsonValueKind.False));
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<NodeApplyResult> ApplyAsync(RemoteMachine machine, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(machine, HttpMethod.Post, "/api/update/apply?trigger=rollout", ct);
            if (response is null)
                return new NodeApplyResult(NodeApplyOutcome.Unreachable);
            if (response.IsSuccessStatusCode)
                return new NodeApplyResult(NodeApplyOutcome.Accepted);

            var reason = await ErrorOf(response, ct);
            // 409 with sessions working is a turn that started since we looked; any other 409 is a plain no.
            return response.StatusCode == HttpStatusCode.Conflict && reason?.Contains("working", StringComparison.OrdinalIgnoreCase) == true
                ? new NodeApplyResult(NodeApplyOutcome.Busy, reason)
                : new NodeApplyResult(NodeApplyOutcome.Refused, reason);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new NodeApplyResult(NodeApplyOutcome.Unreachable);
        }
    }

    public async Task<bool> PushRuleAsync(RemoteMachine machine, string rule, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(machine, HttpMethod.Put, "/api/update/rule", ct, new StringContent($"{{\"rule\":\"{rule}\"}}", System.Text.Encoding.UTF8, "application/json"));
            return response is { IsSuccessStatusCode: true };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage?> SendAsync(RemoteMachine machine, HttpMethod method, string path, CancellationToken ct, HttpContent? body = null)
    {
        if (machines.TryTokenOf(machine) is not { } token)
            return null;

        using var request = new HttpRequestMessage(method, machine.BaseUrl + path) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await httpClients.CreateClient(RemoteMachineService.HttpClientName).SendAsync(request, ct);
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return Text(body.RootElement, "error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
