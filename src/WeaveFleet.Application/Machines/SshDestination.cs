using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// Where to log in over SSH: an <c>~/.ssh/config</c> alias, a host name or address, with an optional user and port
/// (<c>alice@falcon:2222</c>). Parsing is strict because the text ends up as an argument to the user's own
/// <c>ssh</c>: no leading dash, no spaces, no shell characters.
/// </summary>
public sealed partial record SshDestination(string Host, string? User, int? Port)
{
    /// <summary>The destination as the user wrote it, normalized: <c>user@host</c> or <c>user@host:port</c>.</summary>
    public string Text => (User is null ? "" : $"{User}@") + (Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host) + (Port is { } port ? $":{port}" : "");

    /// <summary>What goes to <c>ssh</c> as the destination, without the port (that is <c>-p</c>).</summary>
    public string Target => (User is null ? "" : $"{User}@") + Host;

    public static SshDestination? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        string? user = null;
        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            user = text[..at];
            text = text[(at + 1)..];
            if (!SafeName().IsMatch(user))
                return null;
        }

        int? port = null;
        string host;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
                return null;
            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.Length > 0 && !TryPort(rest, out port))
                return null;
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon >= 0 && text.IndexOf(':', StringComparison.Ordinal) == colon)
            {
                host = text[..colon];
                if (!TryPort(text[colon..], out port))
                    return null;
            }
            else
                host = text;
        }

        if (host.Length == 0 || host.StartsWith('-') || !SafeHost().IsMatch(host))
            return null;
        return new SshDestination(host, user, port);
    }

    private static bool TryPort(string colonAndPort, out int? port)
    {
        port = null;
        if (!colonAndPort.StartsWith(':') || !int.TryParse(colonAndPort.AsSpan(1), out var parsed) || parsed is < 1 or > 65535)
            return false;
        port = parsed;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._-]*$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^[A-Za-z0-9_:.%-]+$")]
    private static partial Regex SafeHost();
}

/// <summary>A host from <c>~/.ssh/config</c>: <paramref name="Alias"/> is what to type after <c>ssh</c>.</summary>
public sealed record SshConfigHost(string Alias, string? HostName, string? User, int? Port);

/// <summary>Reads the <c>Host</c> entries of an OpenSSH config. Wildcard patterns and negations are skipped.</summary>
public static class SshConfigHosts
{
    public static IReadOnlyList<SshConfigHost> Parse(string text)
    {
        var hosts = new List<SshConfigHost>();
        List<int>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var split = line.IndexOfAny([' ', '\t', '=']);
            if (split < 0)
                continue;
            var key = line[..split];
            var value = line[(split + 1)..].Trim().TrimStart('=').Trim();

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                current = [];
                foreach (var pattern in value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (pattern.IndexOfAny(['*', '?', '!']) >= 0 || SshDestination.Parse(pattern) is not { User: null, Port: null })
                        continue;
                    hosts.Add(new SshConfigHost(pattern, null, null, null));
                    current.Add(hosts.Count - 1);
                }
            }
            else if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
                current = null;
            else if (current is not null)
            {
                foreach (var index in current)
                {
                    var host = hosts[index];
                    if (key.Equals("HostName", StringComparison.OrdinalIgnoreCase) && host.HostName is null)
                        hosts[index] = host with { HostName = value };
                    else if (key.Equals("User", StringComparison.OrdinalIgnoreCase) && host.User is null)
                        hosts[index] = host with { User = value };
                    else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && host.Port is null && int.TryParse(value, out var port))
                        hosts[index] = host with { Port = port };
                }
            }
        }

        return hosts;
    }

    /// <summary>The hosts in <c>~/.ssh/config</c> of the user running Fleet; empty when there is none.</summary>
    public static IReadOnlyList<SshConfigHost> ReadDefault()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>What running one command over SSH gave. <paramref name="ExitCode"/> is -1 when <c>ssh</c> couldn't start.</summary>
public sealed record SshResult(int ExitCode, string Output, string Error);

/// <summary>Runs one command on another machine with the user's own <c>ssh</c>: keys and agent, never a password prompt.</summary>
public interface ISshRunner
{
    /// <param name="timeout">How long the command may run; a short default fits a quick question, an install needs more.</param>
    Task<SshResult> RunAsync(SshDestination destination, string command, CancellationToken cancellationToken, TimeSpan? timeout = null);
}

/// <summary>The <c>ssh -N -L</c> tunnels to machines added Over SSH. The machine's address is the tunnel's local port.</summary>
public interface ISshTunnels
{
    /// <summary>
    /// Opens a tunnel to the node port on <paramref name="destination"/> and waits until it carries traffic. Reuses
    /// <paramref name="preferredPort"/> when it is free. Returns the local port, or what to tell the user.
    /// </summary>
    Task<(int Port, string? Error)> OpenAsync(string key, SshDestination destination, int? preferredPort, CancellationToken cancellationToken);

    /// <summary>Stops the tunnel, if there is one, and kills its ssh.</summary>
    void Close(string key);

    /// <summary>Whether the tunnel is carrying traffic right now.</summary>
    bool IsUp(string key);
}
