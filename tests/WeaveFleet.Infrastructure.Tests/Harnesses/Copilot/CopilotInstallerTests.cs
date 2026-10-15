using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Copilot;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Copilot;

public sealed class CopilotInstallerTests : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private readonly string _home = Directory.CreateTempSubdirectory("copilot-install").FullName;
    private readonly List<string> _urls = [];
    private readonly Dictionary<string, byte[]> _files = new();
    private readonly List<string> _reports = [];

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public async Task Installs_both_at_the_pinned_versions_after_checking_their_checksums()
    {
        Publish();
        var installer = NewInstaller();

        await installer.InstallAsync(new Reports(_reports), CancellationToken.None);

        installer.ManagedWeaveCopilot().ShouldBe(Path.Combine(_home, ".weave", "runtimes", "copilot", "weave-copilot", CopilotInstaller.WeaveCopilotVersion, Windows ? "weave-copilot.exe" : "weave-copilot"));
        installer.ManagedCli().ShouldBe(Path.Combine(_home, ".weave", "runtimes", "copilot", "cli", CopilotInstaller.CliVersion, Windows ? "copilot.exe" : "copilot"));
        File.ReadAllText(installer.ManagedCli()!).ShouldBe("cli");
        _urls.ShouldContain(url => url.StartsWith($"https://mirror.test/wc/v{CopilotInstaller.WeaveCopilotVersion}/weave-copilot-{CopilotInstaller.WeaveCopilotVersion}-", StringComparison.Ordinal));
        _urls.ShouldContain(url => url.StartsWith($"https://mirror.test/cli/v{CopilotInstaller.CliVersion}/copilot-", StringComparison.Ordinal));
        _reports.ShouldContain(line => line.StartsWith("Downloading the Copilot CLI", StringComparison.Ordinal));
        Directory.EnumerateDirectories(Path.Combine(_home, ".weave", "runtimes", "copilot"), ".staging-*").ShouldBeEmpty();
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(installer.ManagedCli()!).HasFlag(UnixFileMode.UserExecute).ShouldBeTrue();
    }

    [Fact]
    public async Task Leaves_an_installed_piece_alone_and_fetches_the_missing_one()
    {
        Publish();
        var installer = NewInstaller();
        await installer.InstallAsync(new Reports(_reports), CancellationToken.None);
        Directory.Delete(Path.Combine(_home, ".weave", "runtimes", "copilot", "cli"), recursive: true);
        _urls.Clear();

        await installer.InstallAsync(new Reports(_reports), CancellationToken.None);

        _urls.ShouldAllBe(url => url.StartsWith("https://mirror.test/cli/", StringComparison.Ordinal));
        installer.ManagedCli().ShouldNotBeNull();
    }

    [Fact]
    public async Task Says_the_release_may_not_be_published_when_GitHub_answers_404()
    {
        Publish();
        _files.Keys.Where(key => key.Contains("/wc/", StringComparison.Ordinal)).ToList().ForEach(key => _files.Remove(key));
        var installer = NewInstaller();

        var failure = await Should.ThrowAsync<HarnessInstallException>(() => installer.InstallAsync(new Reports(_reports), CancellationToken.None));

        failure.Message.ShouldBe($"Couldn't download weave-copilot {CopilotInstaller.WeaveCopilotVersion}: GitHub answered 404. The release may not be published yet.");
        installer.ManagedWeaveCopilot().ShouldBeNull();
        installer.ManagedCli().ShouldNotBeNull(); // the CLI went first and stays
    }

    [Fact]
    public async Task Refuses_a_download_that_does_not_match_its_checksum_and_installs_nothing()
    {
        Publish();
        _files.Keys.Where(key => key.Contains("/copilot-", StringComparison.Ordinal) && !key.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)).ToList()
            .ForEach(key => _files[key] = [.. _files[key], 0]);
        var installer = NewInstaller();

        var failure = await Should.ThrowAsync<HarnessInstallException>(() => installer.InstallAsync(new Reports(_reports), CancellationToken.None));

        failure.Message.ShouldBe($"The download of the Copilot CLI {CopilotInstaller.CliVersion} didn't match its checksum, so Fleet threw it away. Try again.");
        installer.ManagedCli().ShouldBeNull();
        Directory.EnumerateDirectories(Path.Combine(_home, ".weave", "runtimes", "copilot"), ".staging-*").ShouldBeEmpty();
    }

    [Fact]
    public void Prefers_the_newest_installed_version()
    {
        var root = Path.Combine(_home, ".weave", "runtimes", "copilot", "weave-copilot");
        foreach (var version in new[] { "0.1.0", "0.10.0", "0.2.0" })
        {
            Directory.CreateDirectory(Path.Combine(root, version));
            File.WriteAllText(Path.Combine(root, version, Windows ? "weave-copilot.exe" : "weave-copilot"), "x");
        }

        NewInstaller().ManagedWeaveCopilot()!.ShouldContain("0.10.0");
    }

    /// <summary>Puts both releases on the fake mirror, in the archive format this platform downloads.</summary>
    private void Publish()
    {
        var rid = (Windows ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");
        var cliPlatform = rid.Replace("win-", "win32-").Replace("osx-", "darwin-");
        var extension = Windows ? ".zip" : ".tar.gz";
        var wcAsset = $"weave-copilot-{CopilotInstaller.WeaveCopilotVersion}-{rid}{extension}";
        var cliAsset = $"copilot-{cliPlatform}{extension}";
        var wcBytes = Archive(Windows ? "weave-copilot.exe" : "weave-copilot", "wc");
        var cliBytes = Archive(Windows ? "copilot.exe" : "copilot", "cli");
        var wc = $"https://mirror.test/wc/v{CopilotInstaller.WeaveCopilotVersion}/";
        var cli = $"https://mirror.test/cli/v{CopilotInstaller.CliVersion}/";
        _files[wc + wcAsset] = wcBytes;
        _files[wc + "SHA256SUMS"] = Encoding.UTF8.GetBytes($"{Hash(wcBytes)}  {wcAsset}\n");
        _files[cli + cliAsset] = cliBytes;
        _files[cli + "SHA256SUMS.txt"] = Encoding.UTF8.GetBytes($"{Hash(cliBytes)}  {cliAsset}\n{new string('0', 64)}  other.tar.gz\n");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Archive(string executable, string content)
    {
        using var output = new MemoryStream();
        if (Windows)
        {
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            using var writer = new StreamWriter(zip.CreateEntry(executable).Open());
            writer.Write(content);
        }
        else
        {
            using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
            using var tar = new TarWriter(gzip);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "THIRD-PARTY-NOTICES") { DataStream = new MemoryStream("n"u8.ToArray()) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, executable) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        }

        return output.ToArray();
    }

    private CopilotInstaller NewInstaller() => new(new Mirror(this), _home, name => name switch
    {
        CopilotInstaller.WeaveCopilotBaseUrlVariable => "https://mirror.test/wc",
        CopilotInstaller.CliBaseUrlVariable => "https://mirror.test/cli",
        _ => null,
    });

    private sealed class Reports(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }

    private sealed class Mirror(CopilotInstallerTests test) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(test));
    }

    private sealed class Handler(CopilotInstallerTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            test._urls.Add(url);
            return Task.FromResult(test._files.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
