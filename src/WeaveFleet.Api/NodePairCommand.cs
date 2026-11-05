using System.Net.Http.Headers;
using System.Text.Json;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Api;

/// <summary>
/// <c>fleet node pair</c>: asks the node that is running here for a one-time code and prints it as a pairing link. The code lasts 10 minutes and works once; whoever redeems it gets a token of their own that this node lists
/// and can remove, so the master token never moves. The request goes to this machine over loopback, signed with the
/// machine token that sits next to the database (or <c>WEAVE_FLEET_AUTH_TOKEN</c>).
/// </summary>
public static class NodePairCommand
{
    public const string Flag = "--node-pair";

    public static async Task<int> RunAsync(
        string host,
        int port,
        string databasePath,
        string? tokenFromEnvironment,
        HttpClient http,
        TextWriter output,
        TextWriter error,
        bool asJson = false,
        CancellationToken cancellationToken = default)
    {
        var token = !string.IsNullOrWhiteSpace(tokenFromEnvironment)
            ? tokenFromEnvironment.Trim()
            : new MachineIdentityStore(databasePath).Get().AccessToken;

        // A wildcard bind answers on loopback; a specific address answers only there.
        var target = host is "0.0.0.0" or "::" or "[::]" or "" ? "127.0.0.1" : host.Trim('[', ']');
        var url = $"http://{(target.Contains(':') ? $"[{target}]" : target)}:{port}/api/machine/pairing/code";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        string body;
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                await error.WriteLineAsync($"The node on port {port} answered {(int)response.StatusCode}. "
                    + "Run fleet node pair with the same --port, --data-dir and --profile the node was started with.");
                return 1;
            }

            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await error.WriteLineAsync($"No Fleet answered on port {port}. Start the node first (fleet node, or fleet node install-service), "
                + "then run fleet node pair with the same --port, --data-dir and --profile.");
            return 1;
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var code = root.GetProperty("manualCode").GetString();
        var name = root.GetProperty("machineName").GetString();
        var addresses = root.GetProperty("addresses").EnumerateArray().Select(a => a.GetString()!).ToList();
        var link = root.TryGetProperty("link", out var linkElement) ? linkElement.GetString() : null;
        if (addresses.Count == 0)
            addresses.Add($"http://localhost:{port}");

        if (asJson)
        {
            // One line for another Fleet reading it over SSH (Add a machine → Over SSH).
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("code", code);
                writer.WriteString("machineName", name);
                if (link is not null)
                    writer.WriteString("link", link);
                writer.WriteStartArray("addresses");
                foreach (var address in addresses)
                    writer.WriteStringValue(address);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            await output.WriteLineAsync(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            return 0;
        }

        if (link is null)
        {
            await output.WriteLineAsync($"{name} only listens on this computer, so no other machine can reach it yet.");
            await output.WriteLineAsync("Start it with --host 0.0.0.0, or put tailscale serve in front of it, then run fleet node pair again.");
            return 0;
        }

        await output.WriteLineAsync($"Pairing link for {name}");
        await output.WriteLineAsync();
        await output.WriteLineAsync($"    {link}");
        await output.WriteLineAsync();
        await output.WriteLineAsync("In the Fleet you work from, open Settings → Machines → Add a machine and paste it.");
        await output.WriteLineAsync("The link works once and expires in 10 minutes. It adds this machine with a key of its own,");
        await output.WriteLineAsync("which you can remove here later.");
        if (addresses.Count > 1)
        {
            await output.WriteLineAsync();
            await output.WriteLineAsync("Also reachable at");
            foreach (var address in addresses.Skip(1))
                await output.WriteLineAsync($"    {address}");
        }

        return 0;
    }
}
