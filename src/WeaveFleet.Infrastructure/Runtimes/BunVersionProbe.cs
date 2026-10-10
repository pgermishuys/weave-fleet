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

    /// <summary>How long stdout gets to finish once the Bun has exited, before what was read so far is used.</summary>
    private static readonly TimeSpan OutputGrace = TimeSpan.FromMilliseconds(250);

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

        if (!OperatingSystem.IsWindows() && HasNoExecuteBit(executablePath))
            return Failed($"Bun at {executablePath} isn't executable (chmod +x {executablePath}).");

        // An empty folder of its own, so a Bun run from anywhere never finds files next to it, or Fleet's, to read.
        var workingFolder = Path.Combine(Path.GetTempPath(), $"fleet-bun-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(workingFolder);
            return await RunInAsync(executablePath, workingFolder, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(workingFolder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: it's empty and in the temp folder.
            }
        }
    }

    private static bool HasNoExecuteBit(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return false;

            const UnixFileMode Execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return File.Exists(path) && (File.GetUnixFileMode(path) & Execute) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<BunProbeResult> RunInAsync(string executablePath, string workingFolder, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingFolder,
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

            var output = new OutputReader(process);
            Task? stdout = null;
            Task? stderr = null;
            try
            {
                process.StandardInput.Close();
                stdout = output.ReadAsync(limit.Token);
                stderr = DiscardAsync(process.StandardError.BaseStream, limit.Token);

                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);

                // A Bun can print its version and exit while a child it started still holds stdout open, so the pipe
                // never closes. Give the output a moment to finish, then use what was read and kill what's left.
                // Known limit: a child that double-forked out of the process tree escapes the kill and can outlive the probe.
                await Task.WhenAny(stdout, Task.Delay(OutputGrace, limit.Token)).ConfigureAwait(false);
                var (bytes, tooMuch) = output.Snapshot();
                Kill(process);

                if (tooMuch)
                    return Failed("Bun printed far more than a version number.");
                if (process.ExitCode != 0)
                    return Failed($"Bun exited with code {process.ExitCode}.");

                var text = Encoding.UTF8.GetString(bytes).TrimEnd();
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
            finally
            {
                // End both readers without waiting on a pipe a stray child might still hold.
                await limit.CancelAsync().ConfigureAwait(false);
                var readers = new[] { stdout, stderr }.OfType<Task>().ToArray();
                try
                {
                    await Task.WhenAny(Task.WhenAll(readers), Task.Delay(OutputGrace, CancellationToken.None)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // The readers were stopped on purpose.
                }

                foreach (var reader in readers)
                    _ = reader.Exception;
            }
        }
    }

    private static BunProbeResult Failed(string error) => new(null, error);

    /// <summary>Reads stdout up to <see cref="MaxOutputBytes"/>, keeping what it has so far where another thread can take it.</summary>
    private sealed class OutputReader(Process process)
    {
        private readonly object _lock = new();
        private readonly byte[] _buffer = new byte[MaxOutputBytes + 1];
        private int _length;
        private bool _tooMuch;

        /// <summary>Reads until the pipe closes; one byte over the limit kills the process so it can't flood.</summary>
        public async Task ReadAsync(CancellationToken ct)
        {
            var stream = process.StandardOutput.BaseStream;
            var chunk = new byte[1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                    return;

                lock (_lock)
                {
                    var room = Math.Min(read, _buffer.Length - _length);
                    Array.Copy(chunk, 0, _buffer, _length, room);
                    _length += room;
                    _tooMuch = _length > MaxOutputBytes;
                }

                if (_tooMuch)
                {
                    Kill(process);
                    return;
                }
            }
        }

        /// <summary>What has been read so far.</summary>
        public (byte[] Bytes, bool TooMuch) Snapshot()
        {
            lock (_lock)
                return (_tooMuch ? [] : _buffer[.._length], _tooMuch);
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
