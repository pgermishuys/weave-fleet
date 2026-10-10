using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>What running <c>{bun} --version</c> said.</summary>
/// <param name="Version">The version it printed, when it ran, exited 0 and printed exactly one version.</param>
/// <param name="Error">Why there's no version, in a sentence fit to show the user; <see langword="null"/> with one.</param>
internal sealed record BunProbeResult(BunVersion? Version, string? Error);

/// <summary>
/// Runs <c>{bun} --version</c> to learn a Bun's version: directly, with no shell, a clean environment and a time
/// limit, reading at most a little output, and parsing it strictly.
/// </summary>
internal static class BunVersionProbe
{
    /// <summary>How long <c>--version</c> may take before Fleet gives up on it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The most stdout read; a Bun that prints more is not printing a version.</summary>
    private const int MaxOutputBytes = 4096;

    /// <summary>The most characters of a Bun's output quoted back to the user.</summary>
    private const int MaxExcerpt = 40;

    /// <summary>Runs <paramref name="executablePath"/> <c>--version</c>. Never throws for a Bun that won't run.</summary>
    /// <param name="executablePath">The Bun to run, started directly.</param>
    /// <param name="timeout">How long it may take; past that the process and everything it started are killed.</param>
    /// <param name="ct">Cancelling kills the process and throws <see cref="OperationCanceledException"/>.</param>
    public static async Task<BunProbeResult> RunAsync(string executablePath, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? "",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--version");

        // Nothing of Fleet's environment (tokens, PATH, proxies) reaches a Bun Fleet is only checking.
        psi.Environment.Clear();
        if (OperatingSystem.IsWindows())
        {
            // A Windows process can't start without these.
            foreach (var name in new[] { "SystemRoot", "windir" })
            {
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    psi.Environment[name] = value;
            }
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("No process started.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Failed($"Bun couldn't start: {ex.Message}");
        }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);

            try
            {
                process.StandardInput.Close();
                var stdout = ReadVersionOutputAsync(process, limit.Token);
                var stderr = DiscardAsync(process.StandardError.BaseStream, limit.Token);

                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                var output = await stdout.ConfigureAwait(false);
                await stderr.ConfigureAwait(false);

                if (output.TooMuch)
                    return Failed("Bun printed far more than a version number.");
                if (process.ExitCode != 0)
                    return Failed($"Bun exited with code {process.ExitCode}.");

                var text = Encoding.UTF8.GetString(output.Bytes).TrimEnd();
                return BunVersion.TryParse(text, out var version)
                    ? new BunProbeResult(version, null)
                    : Failed(text.Length == 0
                        ? "Bun printed nothing, which isn't a version."
                        : $"Bun printed \"{Excerpt(text)}\", which isn't a version.");
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                ct.ThrowIfCancellationRequested();
                return Failed($"Bun didn't answer within {timeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} seconds.");
            }
        }
    }

    private static BunProbeResult Failed(string error) => new(null, error);

    /// <summary>Reads stdout up to <see cref="MaxOutputBytes"/>; one byte more kills the process so it can't flood.</summary>
    private static async Task<(byte[] Bytes, bool TooMuch)> ReadVersionOutputAsync(Process process, CancellationToken ct)
    {
        var stream = process.StandardOutput.BaseStream;
        var buffer = new byte[MaxOutputBytes + 1];
        var length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0)
                return (buffer[..length], false);

            length += read;
            if (length > MaxOutputBytes)
            {
                Kill(process);
                return ([], true);
            }
        }
    }

    /// <summary>Reads and throws away a stream, so the child never blocks on a full pipe.</summary>
    private static async Task DiscardAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1024];
        while (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) > 0)
        {
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>A short, single-line piece of <paramref name="text"/> that is safe to show.</summary>
    private static string Excerpt(string text)
    {
        var clean = new StringBuilder();
        foreach (var c in text)
        {
            if (clean.Length >= MaxExcerpt)
                break;
            if (c is '\r' or '\n')
                clean.Append(' ');
            else if (!char.IsControl(c))
                clean.Append(c);
        }

        return clean.ToString();
    }
}
