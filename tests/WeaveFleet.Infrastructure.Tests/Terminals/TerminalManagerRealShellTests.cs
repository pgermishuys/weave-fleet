using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Terminals;
using WeaveFleet.Infrastructure.Terminals;

namespace WeaveFleet.Infrastructure.Tests.Terminals;

/// <summary>The terminal manager with a real shell, real files and a real pseudoterminal. Linux only.</summary>
public sealed class TerminalManagerRealShellTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _folder = Directory.CreateTempSubdirectory("fleet-session-").FullName;
    private readonly string _history = Directory.CreateTempSubdirectory("fleet-terminals-").FullName;
    private readonly List<TerminalManager> _managers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var manager in _managers)
            await manager.DisposeAsync();
        Environment.SetEnvironmentVariable("Fleet__Auth__ClientSecret", null);
        foreach (var dir in new[] { _folder, _history })
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AShellRunsInTheSessionFolder_AndItsOutputSurvivesARestart()
    {
        if (!OperatingSystem.IsLinux()) return;
        var context = new TerminalContext("s1", "u1", _folder);

        var manager = NewManager();
        var created = await manager.CreateAsync(context, 100, 30);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        using (var attachment = (await manager.AttachAsync(context, created.Value.Id, 100, 30)).Value!)
        {
            await attachment.WriteAsync(Encoding.UTF8.GetBytes("echo \"in:$(pwd)\"; echo secret=[$Fleet__Auth__ClientSecret]\r"));
            var output = await ReadUntilAsync(attachment, "secret=[]");
            output.ShouldContain("in:" + _folder);
        }

        await manager.ShutdownAsync();
        var restarted = NewManager();
        (await restarted.ListAsync("s1")).Single().Status.ShouldBe(TerminalStatus.Stopped);
        using var reopened = (await restarted.AttachAsync(context, created.Value.Id, 100, 30)).Value!;

        var replay = Encoding.UTF8.GetString(reopened.Replay);
        replay.ShouldContain("in:" + _folder);
        replay.ShouldContain("Fleet restarted");
        await reopened.WriteAsync("echo back-again\r"u8.ToArray());
        (await ReadUntilAsync(reopened, "back-again\r\n")).ShouldContain("back-again");
    }

    private TerminalManager NewManager()
    {
        Environment.SetEnvironmentVariable("Fleet__Auth__ClientSecret", "must-not-leak");
        var options = new FleetOptions();
        options.Terminal.HistoryDirectory = _history;
        var manager = new TerminalManager(
            new PortaPtyFactory(),
            new TerminalHistoryStore(_history),
            new FakeEventBroadcaster(),
            options,
            NullLogger<TerminalManager>.Instance)
        {
            // Plain sh with no rc file, so the machine's shell setup doesn't matter.
            ProcessEnvironment = () => new System.Collections.Hashtable
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
                ["HOME"] = _folder,
                ["SHELL"] = "/bin/sh",
                ["ENV"] = string.Empty,
                ["Fleet__Auth__ClientSecret"] = "must-not-leak",
            },
        };
        _managers.Add(manager);
        return manager;
    }

    private static async Task<string> ReadUntilAsync(TerminalAttachment attachment, string text)
    {
        var output = new StringBuilder();
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var frame in attachment.Frames.ReadAllAsync(cts.Token))
        {
            if (frame.Data is not null)
                output.Append(Encoding.UTF8.GetString(frame.Data));
            if (output.ToString().Contains(text, StringComparison.Ordinal))
                return output.ToString();
        }
        throw new TimeoutException($"Never saw '{text}'. Output: {output}");
    }
}
