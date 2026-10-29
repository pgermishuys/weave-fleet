using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.Copilot;

/// <summary>
/// Installs weave-copilot and the Copilot CLI it drives under <c>~/.weave/runtimes/copilot/{weave-copilot,cli}/{version}/</c>.
/// Each is the version this Fleet release pins: weave-copilot from its GitHub release, the CLI from GitHub's own
/// (it isn't bundled with weave-copilot). A download is checked against the release's SHA256SUMS, unpacked beside its
/// final place and moved in whole, so a half-finished install is never mistaken for a good one.
/// </summary>
internal sealed class CopilotInstaller(IHttpClientFactory httpClientFactory, string home, Func<string, string?>? environment = null) : IHarnessInstaller
{
    /// <summary>The weave-copilot release this Fleet was built against.</summary>
    internal const string WeaveCopilotVersion = "0.2.1";

    /// <summary>The Copilot CLI release weave-copilot was tested with.</summary>
    internal const string CliVersion = "1.0.105";

    internal const string WeaveCopilotBaseUrlVariable = "FLEET_WEAVE_COPILOT_RELEASE_BASE_URL";
    internal const string CliBaseUrlVariable = "FLEET_COPILOT_CLI_RELEASE_BASE_URL";
    private const string WeaveCopilotBaseUrl = "https://github.com/pgermishuys/weave-copilot/releases/download";
    private const string CliBaseUrl = "https://github.com/github/copilot-cli/releases/download";

    private static readonly bool Windows = OperatingSystem.IsWindows();

    private string? Env(string name) => (environment ?? Environment.GetEnvironmentVariable)(name);

    private string Root => Path.Combine(home, ".weave", "runtimes", "copilot");

    /// <inheritdoc />
    public string TargetVersion => WeaveCopilotVersion;

    /// <inheritdoc />
    public string Plan => $"Fleet downloads weave-copilot {WeaveCopilotVersion} and the Copilot CLI {CliVersion} from GitHub into {Root}.";

    /// <summary>The newest weave-copilot Fleet installed, or <see langword="null"/>.</summary>
    internal string? ManagedWeaveCopilot() => Newest("weave-copilot", Windows ? "weave-copilot.exe" : "weave-copilot");

    /// <summary>The newest Copilot CLI Fleet installed, or <see langword="null"/>.</summary>
    internal string? ManagedCli() => Newest("cli", Windows ? "copilot.exe" : "copilot");

    private string? Newest(string kind, string file)
    {
        var folder = Path.Combine(Root, kind);
        if (!Directory.Exists(folder))
            return null;
        return Directory.EnumerateDirectories(folder)
            .Select(directory => (Directory: directory, File: Path.Combine(directory, file)))
            .Where(candidate => File.Exists(candidate.File))
            .OrderByDescending(candidate => Path.GetFileName(candidate.Directory), Comparer<string>.Create(HarnessVersion.Compare))
            .Select(candidate => candidate.File)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task InstallAsync(IProgress<string> progress, CancellationToken ct)
    {
        var (os, arch) = Platform();
        var rid = $"{os}-{arch}";
        var cliPlatform = $"{(os == "osx" ? "darwin" : os == "win" ? "win32" : os)}-{arch}";

        // The CLI first: weave-copilot is what Fleet looks for, so it only shows up once it has the CLI to drive.
        if (!File.Exists(Path.Combine(Root, "cli", CliVersion, Windows ? "copilot.exe" : "copilot")))
        {
            var asset = $"copilot-{cliPlatform}{(os == "win" ? ".zip" : ".tar.gz")}";
            var baseUrl = Env(CliBaseUrlVariable) is { Length: > 0 } custom ? custom : CliBaseUrl;
            await InstallAsync("the Copilot CLI", CliVersion, $"{baseUrl.TrimEnd('/')}/v{CliVersion}", "SHA256SUMS.txt", asset,
                Path.Combine(Root, "cli", CliVersion), Windows ? "copilot.exe" : "copilot", progress, ct).ConfigureAwait(false);
        }

        if (!File.Exists(Path.Combine(Root, "weave-copilot", WeaveCopilotVersion, Windows ? "weave-copilot.exe" : "weave-copilot")))
        {
            var asset = $"weave-copilot-{WeaveCopilotVersion}-{rid}{(os == "win" ? ".zip" : ".tar.gz")}";
            var baseUrl = Env(WeaveCopilotBaseUrlVariable) is { Length: > 0 } custom ? custom : WeaveCopilotBaseUrl;
            await InstallAsync("weave-copilot", WeaveCopilotVersion, $"{baseUrl.TrimEnd('/')}/v{WeaveCopilotVersion}", "SHA256SUMS", asset,
                Path.Combine(Root, "weave-copilot", WeaveCopilotVersion), Windows ? "weave-copilot.exe" : "weave-copilot", progress, ct).ConfigureAwait(false);
        }
    }

    private async Task InstallAsync(
        string name, string version, string releaseUrl, string sumsFile, string asset, string destination, string executable,
        IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var staging = Path.Combine(Root, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            progress.Report($"Downloading {name} {version}…");
            var sums = System.Text.Encoding.UTF8.GetString(await DownloadAsync(name, version, $"{releaseUrl}/{sumsFile}", null, ct).ConfigureAwait(false));
            var expected = sums.Split('\n').Select(line => line.Split([' ', '*'], StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(parts => parts.Length == 2 && parts[1].Trim() == asset)?[0]
                ?? throw new HarnessInstallException($"Couldn't install {name} {version}: the release's {sumsFile} doesn't list {asset}.");

            var archive = await DownloadAsync(name, version, $"{releaseUrl}/{asset}", percent => progress.Report($"Downloading {name} {version}… {percent}%"), ct).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(archive)), expected, StringComparison.OrdinalIgnoreCase))
                throw new HarnessInstallException($"The download of {name} {version} didn't match its checksum, so Fleet threw it away. Try again.");

            progress.Report($"Installing {name} {version}…");
            var target = Path.Combine(staging, executable);
            await ExtractAsync(archive, asset.EndsWith(".zip", StringComparison.Ordinal), executable, target, ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            Directory.Move(staging, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new HarnessInstallException($"Couldn't install {name} {version}: {ex.Message}", ex);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
        }
    }

    private async Task<byte[]> DownloadAsync(string name, string version, string url, Action<int>? percent, CancellationToken ct)
    {
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan; // the install as a whole has its own limit
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode == System.Net.HttpStatusCode.NotFound ? " The release may not be published yet." : string.Empty;
                throw new HarnessInstallException($"Couldn't download {name} {version}: GitHub answered {(int)response.StatusCode}.{hint}");
            }

            var total = response.Content.Headers.ContentLength;
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            var last = -1;
            int read;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                memory.Write(buffer, 0, read);
                if (percent is not null && total is > 0 && (int)(memory.Length * 100 / total.Value) is var now && now != last)
                    percent(last = now);
            }

            return memory.ToArray();
        }
        catch (HttpRequestException ex)
        {
            throw new HarnessInstallException($"Couldn't download {name} {version} from {new Uri(url).Host}: {ex.Message}", ex);
        }
    }

    /// <summary>Copies the one executable out of the archive; no other path in it is ever written.</summary>
    private static async Task ExtractAsync(byte[] archive, bool zip, string executable, string target, CancellationToken ct)
    {
        await using var source = new MemoryStream(archive);
        if (zip)
        {
            using var reader = new ZipArchive(source, ZipArchiveMode.Read);
            var entry = reader.Entries.FirstOrDefault(e => e.Name == executable)
                ?? throw new InvalidDataException($"the download has no {executable} in it");
            await using var from = entry.Open();
            await using var to = File.Create(target);
            await from.CopyToAsync(to, ct).ConfigureAwait(false);
            return;
        }

        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } found)
        {
            if (found.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || Path.GetFileName(found.Name) != executable)
                continue;
            await using var to = File.Create(target);
            await found.DataStream!.CopyToAsync(to, ct).ConfigureAwait(false);
            return;
        }

        throw new InvalidDataException($"the download has no {executable} in it");
    }

    private static (string Os, string Arch) Platform()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => throw new HarnessInstallException($"Fleet can't install GitHub Copilot on a {other} computer."),
        };
        return (os, arch);
    }
}
