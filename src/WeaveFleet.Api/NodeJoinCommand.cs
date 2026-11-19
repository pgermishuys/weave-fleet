using System.Net.Http.Headers;
using System.Text.Json;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Api;

/// <summary>
/// <c>fleet node join '&lt;address&gt;#CODE'</c>: asks the machine running here to join the Fleet that made the join line.
/// The request goes to this machine over loopback, signed with the machine token that sits next to the database, so
/// the join code is the only secret that crosses the network. The line comes from Add a machine → On a new server, and
/// <c>install.sh --join</c> runs this after it has started the machine.
/// </summary>
public static class NodeJoinCommand
{
    public const string Flag = "--node-join";

    /// <param name="wait">How long to keep asking while the machine isn't answering yet (it was just started).</param>
    /// <param name="retryEvery">The pause between asks.</param>
    public static async Task<int> RunAsync(
        string target,
        string host,
        int port,
        string databasePath,
        string? tokenFromEnvironment,
        HttpClient http,
        TextWriter output,
        TextWriter error,
        TimeSpan wait,
        TimeSpan retryEvery,
        CancellationToken cancellationToken = default)
    {
        var token = !string.IsNullOrWhiteSpace(tokenFromEnvironment)
            ? tokenFromEnvironment.Trim()
            : new MachineIdentityStore(databasePath).Get().AccessToken;

        var local = host is "0.0.0.0" or "::" or "[::]" or "" ? "127.0.0.1" : host.Trim('[', ']');
        var url = $"http://{(local.Contains(':') ? $"[{local}]" : local)}:{port}/api/machine/join";
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["target"] = target });

        var deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

            try
            {
                using var response = await http.SendAsync(request, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                    return await ReportJoinedAsync(text, output);

                await error.WriteLineAsync($"Couldn't join: {MessageOf(text, response.StatusCode)}");
                return 1;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    await error.WriteLineAsync($"No Fleet answered on port {port}. Start this machine first (fleet node up), "
                        + "then run fleet node join with the same --port, --data-dir and --profile.");
                    return 1;
                }

                await Task.Delay(retryEvery, cancellationToken);
            }
        }
    }

    private static async Task<int> ReportJoinedAsync(string text, TextWriter output)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var name = root.TryGetProperty("fleetMachineName", out var nameElement) ? nameElement.GetString() : null;
        var listed = root.TryGetProperty("listed", out var listedElement) && listedElement.ValueKind == JsonValueKind.True;
        var keepsList = root.TryGetProperty("fleetKeepsList", out var keepsElement) && keepsElement.ValueKind == JsonValueKind.True;
        name ??= "your Fleet";

        await output.WriteLineAsync($"✓ Added to {name}");
        if (listed)
            await output.WriteLineAsync($"✓ {name} is in this machine's list too. {name} tells the rest of your Fleet about it.");
        else if (keepsList)
            await output.WriteLineAsync($"  {name} is telling the rest of your Fleet about this machine. It shows up everywhere in a moment.");
        else
            await output.WriteLineAsync($"  Your Fleet is off on {name}, so only {name} lists this machine. Turn it on in Settings → Labs there to share it with the rest.");
        return 0;
    }

    private static string MessageOf(string text, System.Net.HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            foreach (var key in new[] { "message", "error" })
            {
                if (document.RootElement.TryGetProperty(key, out var value) && value.GetString() is { Length: > 0 } message)
                    return message;
            }
        }
        catch (JsonException)
        {
        }

        return $"this machine answered {(int)status}. Run fleet node join with the same --port, --data-dir and --profile you started fleet node with.";
    }
}
