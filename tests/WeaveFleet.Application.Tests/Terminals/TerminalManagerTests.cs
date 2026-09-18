using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Terminals;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Terminals;

public sealed class TerminalManagerTests : IAsyncDisposable
{
    private const string Folder = "/work/weave-fleet";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TerminalContext Ctx = new("s1", "u1", Folder);

    private readonly FakePtyFactory _ptys = new();
    private readonly InMemoryTerminalHistoryStore _store = new();
    private readonly FakeEventBroadcaster _events = new();
    private readonly List<TerminalManager> _managers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var manager in _managers)
            await manager.DisposeAsync();
    }

    [Fact]
    public async Task Create_StartsTheShellInTheSessionFolder_WithoutFleetsOwnVariables()
    {
        if (OperatingSystem.IsWindows()) return;
        var manager = NewManager();

        var created = await manager.CreateAsync(Ctx, 100, 30);

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        created.Value.Title.ShouldBe("zsh");
        created.Value.Status.ShouldBe(TerminalStatus.Running);
        var spawn = _ptys.Spawned.Single().Options;
        spawn.Shell.ShouldBe("/bin/zsh");
        spawn.Args.ShouldBe(["-l"]);
        spawn.WorkingDirectory.ShouldBe(Folder);
        (spawn.Cols, spawn.Rows).ShouldBe((100, 30));
        spawn.Environment.Keys.ShouldNotContain("Fleet__Auth__ClientSecret");
        spawn.Environment.Keys.ShouldNotContain("ASPNETCORE_URLS");
        spawn.Environment.Keys.ShouldNotContain("URLS");
        spawn.Environment["PATH"].ShouldBe("/usr/bin");
        spawn.Environment["TERM"].ShouldBe("xterm-256color");
        (await _store.ListAsync("s1")).Single().Id.ShouldBe(created.Value.Id);
        var opened = _events.Broadcasts.Single();
        opened.Topic.ShouldBe("session:s1");
        opened.Type.ShouldBe("terminal.opened");
        opened.UserId.ShouldBe("u1");
        opened.DomainEvent.ShouldBeOfType<TerminalOpened>().Payload.TerminalId.ShouldBe(created.Value.Id);
    }

    [Fact]
    public async Task Create_NamesASecondTerminalAfterTheFirst()
    {
        if (OperatingSystem.IsWindows()) return;
        var manager = NewManager();

        await manager.CreateAsync(Ctx, 80, 24);
        var second = await manager.CreateAsync(Ctx, 80, 24);

        second.Value!.Title.ShouldBe("zsh 2");
    }

    [Fact]
    public async Task Create_TriesTheNextShell_WhenOneWontStart()
    {
        if (OperatingSystem.IsWindows()) return;
        _ptys.Fails = options => options.Shell == "/bin/zsh";
        var manager = NewManager();

        var created = await manager.CreateAsync(Ctx, 80, 24);

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
        created.Value.Title.ShouldBe("bash");
    }

    [Fact]
    public async Task Create_Fails_WhenNoShellStarts()
    {
        _ptys.Fails = _ => true;
        var manager = NewManager();

        var created = await manager.CreateAsync(Ctx, 80, 24);

        created.Error!.Kind.ShouldBe(TerminalErrorKind.SpawnFailed);
        (await _store.ListAsync("s1")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_RefusesPastTheSessionLimit()
    {
        var manager = NewManager(o => o.MaxTerminalsPerSession = 1);
        await manager.CreateAsync(Ctx, 80, 24);

        var second = await manager.CreateAsync(Ctx, 80, 24);

        second.Error!.Kind.ShouldBe(TerminalErrorKind.LimitReached);
        second.Error.Message.ShouldContain("Close one");
    }

    [Fact]
    public async Task Create_RefusesPastTheLimitAcrossFleet()
    {
        var manager = NewManager(o => o.MaxLiveTerminals = 1);
        await manager.CreateAsync(Ctx, 80, 24);

        var other = await manager.CreateAsync(Ctx with { SessionId = "s2" }, 80, 24);

        other.Error!.Kind.ShouldBe(TerminalErrorKind.LimitReached);
    }

    [Fact]
    public async Task Create_RefusesWhenTheSessionFolderIsGone()
    {
        var manager = NewManager();

        var created = await manager.CreateAsync(Ctx with { Directory = "/gone" }, 80, 24);

        created.Error!.Kind.ShouldBe(TerminalErrorKind.Unavailable);
        created.Error.Message.ShouldContain("/gone");
        _ptys.Spawned.ShouldBeEmpty();
    }

    [Fact]
    public async Task Attach_GivesTheScrollbackThenLiveOutput()
    {
        var manager = NewManager();
        var created = await manager.CreateAsync(Ctx, 80, 24);
        var shell = _ptys.Spawned.Single();
        shell.Emit("hello\r\n");
        await WaitForReplayAsync(manager, created.Value!.Id, "hello");

        using var attachment = (await manager.AttachAsync(Ctx, created.Value.Id, 120, 40)).Value!;
        shell.Emit("world");

        Text(attachment.Replay).ShouldBe("hello\r\n");
        var frame = await attachment.Frames.ReadAsync().AsTask().WaitAsync(Timeout);
        frame.Kind.ShouldBe(TerminalFrameKind.Output);
        Text(frame.Data!).ShouldBe("world");
        shell.Size.ShouldBe((120, 40));
    }

    [Fact]
    public async Task Attach_SendsInputToTheShell()
    {
        var manager = NewManager();
        var created = await manager.CreateAsync(Ctx, 80, 24);
        using var attachment = (await manager.AttachAsync(Ctx, created.Value!.Id, 80, 24)).Value!;

        await attachment.WriteAsync("ls\r"u8.ToArray());

        Text([.. _ptys.Spawned.Single().Written.SelectMany(w => w)]).ShouldBe("ls\r");
    }

    [Fact]
    public async Task Attach_StartsANewShellUnderTheSavedScrollback_AfterARestart()
    {
        var createdAt = DateTimeOffset.UtcNow.AddHours(-1);
        await _store.SaveTerminalAsync("s1", new SavedTerminal("t_saved", "zsh", createdAt));
        await _store.WriteHistoryAsync("s1", "t_saved", "old output\r\n"u8.ToArray());
        var manager = NewManager();

        (await manager.ListAsync("s1")).Single().Status.ShouldBe(TerminalStatus.Stopped);
        var attached = await manager.AttachAsync(Ctx, "t_saved", 80, 24);

        attached.IsSuccess.ShouldBeTrue(attached.Error?.Message);
        Text(attached.Value.Replay).ShouldBe("old output\r\n" + Text(TerminalManager.RestartDivider));
        attached.Value.Terminal.Title.ShouldBe("zsh");
        _ptys.Spawned.Count.ShouldBe(1);
        (await manager.ListAsync("s1")).Single().Status.ShouldBe(TerminalStatus.Running);
        attached.Value.Dispose();
    }

    [Fact]
    public async Task Attach_IsNotFound_ForAnUnknownTerminal()
    {
        var manager = NewManager();

        var attached = await manager.AttachAsync(Ctx, "t_nope", 80, 24);

        attached.Error!.Kind.ShouldBe(TerminalErrorKind.NotFound);
    }

    [Fact]
    public async Task Close_EndsTheShellAndForgetsTheTerminal()
    {
        var manager = NewManager();
        var created = await manager.CreateAsync(Ctx, 80, 24);
        await _store.WriteHistoryAsync("s1", created.Value!.Id, "x"u8.ToArray());

        (await manager.CloseAsync(Ctx, created.Value.Id)).ShouldBeTrue();

        _ptys.Spawned.Single().Killed.ShouldBeTrue();
        (await manager.ListAsync("s1")).ShouldBeEmpty();
        (await _store.ReadHistoryAsync("s1", created.Value.Id)).ShouldBeNull();
        _events.Broadcasts.Last().Type.ShouldBe("terminal.closed");
        (await manager.CloseAsync(Ctx, created.Value.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task AShellThatEndsOnItsOwn_ClosesItsTab()
    {
        var manager = NewManager();
        var created = await manager.CreateAsync(Ctx, 80, 24);
        using var attachment = (await manager.AttachAsync(Ctx, created.Value!.Id, 80, 24)).Value!;

        _ptys.Spawned.Single().ExitWith(0);

        var frames = await ReadAllAsync(attachment.Frames);
        frames.Last().ShouldBe(TerminalFrame.Exited(0));
        await WaitUntilAsync(() => _events.Broadcasts.Any(b => b.Type == "terminal.closed"));
        _events.Broadcasts.Last().DomainEvent.ShouldBeOfType<TerminalClosed>().Payload.ExitCode.ShouldBe(0);
        (await manager.ListAsync("s1")).ShouldBeEmpty();
        manager.LiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task EndSession_EndsItsShellsAndDeletesItsTerminals()
    {
        var manager = NewManager();
        await manager.CreateAsync(Ctx, 80, 24);
        await manager.CreateAsync(Ctx, 80, 24);
        var other = await manager.CreateAsync(Ctx with { SessionId = "s2" }, 80, 24);

        await manager.EndSessionAsync("s1");

        _ptys.Spawned.Count(p => p.Killed).ShouldBe(2);
        (await manager.ListAsync("s1")).ShouldBeEmpty();
        (await manager.ListAsync("s2")).Single().Id.ShouldBe(other.Value!.Id);
        manager.LiveCount.ShouldBe(1);
    }

    [Fact]
    public async Task Shutdown_SavesTheScrollback_AndTheTerminalComesBackStopped()
    {
        var manager = NewManager();
        var created = await manager.CreateAsync(Ctx, 80, 24);
        _ptys.Spawned.Single().Emit("keep me\r\n");
        await WaitForReplayAsync(manager, created.Value!.Id, "keep me");

        await manager.ShutdownAsync();

        _ptys.Spawned.Single().Killed.ShouldBeTrue();
        Text((await _store.ReadHistoryAsync("s1", created.Value.Id))!).ShouldBe("keep me\r\n");
        var restarted = NewManager();
        (await restarted.ListAsync("s1")).Single().Status.ShouldBe(TerminalStatus.Stopped);
    }

    [Fact]
    public async Task Output_IsSavedShortlyAfterItArrives()
    {
        var manager = NewManager();
        var created = await manager.CreateAsync(Ctx, 80, 24);

        _ptys.Spawned.Single().Emit("saved\r\n");

        await WaitUntilAsync(() => _store.History.TryGetValue(("s1", created.Value!.Id), out var saved) && Text(saved) == "saved\r\n");
    }

    [Fact]
    public async Task Queries_AreKeptOutOfTheScrollback_ButReachLiveClients()
    {
        var manager = NewManager();
        var created = await manager.CreateAsync(Ctx, 80, 24);
        using var attachment = (await manager.AttachAsync(Ctx, created.Value!.Id, 80, 24)).Value!;

        _ptys.Spawned.Single().Emit("a\u001b[6nb");

        var frame = await attachment.Frames.ReadAsync().AsTask().WaitAsync(Timeout);
        Text(frame.Data!).ShouldBe("a\u001b[6nb");
        await WaitForReplayAsync(manager, created.Value.Id, "ab");
    }

    [Fact]
    public async Task Clear_EmptiesTheScrollbackAndTellsEveryClient()
    {
        var manager = NewManager();
        var created = await manager.CreateAsync(Ctx, 80, 24);
        _ptys.Spawned.Single().Emit("noise\r\n");
        await WaitForReplayAsync(manager, created.Value!.Id, "noise");
        using var watcher = (await manager.AttachAsync(Ctx, created.Value.Id, 80, 24)).Value!;

        watcher.Clear();

        (await watcher.Frames.ReadAsync().AsTask().WaitAsync(Timeout)).ShouldBe(TerminalFrame.Cleared);
        using var later = (await manager.AttachAsync(Ctx, created.Value.Id, 80, 24)).Value!;
        later.Replay.ShouldBeEmpty();
    }

    [Fact]
    public async Task AClientThatFallsBehind_IsDropped()
    {
        var manager = NewManager();
        var created = await manager.CreateAsync(Ctx, 80, 24);
        using var slow = (await manager.AttachAsync(Ctx, created.Value!.Id, 80, 24)).Value!;
        var shell = _ptys.Spawned.Single();

        for (var i = 0; i < 200; i++)
            shell.Emit($"line {i}\r\n");
        await WaitForReplayAsync(manager, created.Value.Id, "line 199");

        // It gets what was already queued for it, then the error; a socket closes on that and reconnects.
        await Should.ThrowAsync<TerminalClientTooSlowException>(() => ReadAllAsync(slow.Frames));
    }

    private TerminalManager NewManager(Action<TerminalOptions>? configure = null)
    {
        var options = new FleetOptions();
        configure?.Invoke(options.Terminal);
        var manager = new TerminalManager(_ptys, _store, _events, options, NullLogger<TerminalManager>.Instance)
        {
            DirectoryExists = dir => dir == Folder,
            FileExists = file => file is "/bin/zsh" or "/bin/bash" || file.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase),
            ProcessEnvironment = () => new Hashtable
            {
                ["PATH"] = "/usr/bin",
                ["SHELL"] = "/bin/zsh",
                ["HOME"] = "/home/me",
                ["LANG"] = "en_US.UTF-8",
                ["Fleet__Auth__ClientSecret"] = "secret",
                ["ASPNETCORE_URLS"] = "http://127.0.0.1:3000",
                ["URLS"] = "http://127.0.0.1:3000",
            },
        };
        _managers.Add(manager);
        return manager;
    }

    private static async Task WaitForReplayAsync(TerminalManager manager, string terminalId, string text)
    {
        await WaitUntilAsync(async () =>
        {
            using var probe = (await manager.AttachAsync(Ctx, terminalId, 80, 24)).Value!;
            return Text(probe.Replay).Contains(text, StringComparison.Ordinal);
        });
    }

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Timeout)
        {
            if (await condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Condition not met in time.");
    }

    private static async Task<List<TerminalFrame>> ReadAllAsync(ChannelReader<TerminalFrame> frames)
    {
        var all = new List<TerminalFrame>();
        await foreach (var frame in frames.ReadAllAsync().WithCancellation(new CancellationTokenSource(Timeout).Token))
            all.Add(frame);
        return all;
    }

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private sealed class FakePtyFactory : IPtyFactory
    {
        public ConcurrentQueue<FakePtyProcess> Queue { get; } = new();
        public List<FakePtyProcess> Spawned => [.. Queue];
        public Func<PtySpawnOptions, bool> Fails { get; set; } = _ => false;

        public Task<IPtyProcess> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default)
        {
            if (Fails(options))
                throw new InvalidOperationException($"{options.Shell} won't start");
            var process = new FakePtyProcess(options);
            Queue.Enqueue(process);
            return Task.FromResult<IPtyProcess>(process);
        }
    }

    private sealed class FakePtyProcess(PtySpawnOptions options) : IPtyProcess
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource<PtyExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PtySpawnOptions Options { get; } = options;
        public ConcurrentQueue<byte[]> Written { get; } = new();
        public (int Cols, int Rows) Size { get; private set; } = (options.Cols, options.Rows);
        public bool Killed { get; private set; }
        public int Pid => 4242;
        public Task<PtyExit> Exited => _exit.Task;

        public void Emit(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public void ExitWith(int code)
        {
            _output.Writer.TryComplete();
            _exit.TrySetResult(new PtyExit(code, Killed: false));
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (await _output.Reader.WaitToReadAsync(ct))
            {
                if (_output.Reader.TryRead(out var chunk))
                {
                    chunk.CopyTo(buffer);
                    return chunk.Length;
                }
            }
            return 0;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Written.Enqueue(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public void Resize(int cols, int rows) => Size = (cols, rows);

        public void Kill()
        {
            if (_exit.Task.IsCompleted) return;
            Killed = true;
            _output.Writer.TryComplete();
            _exit.TrySetResult(new PtyExit(null, Killed: true));
        }

        public ValueTask DisposeAsync()
        {
            Kill();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Terminal list and scrollback in memory, for tests.</summary>
internal sealed class InMemoryTerminalHistoryStore : ITerminalHistoryStore
{
    private readonly ConcurrentDictionary<string, List<SavedTerminal>> _terminals = new();

    public ConcurrentDictionary<(string SessionId, string TerminalId), byte[]> History { get; } = new();

    public Task<IReadOnlyList<SavedTerminal>> ListAsync(string sessionId, CancellationToken ct = default)
    {
        var list = _terminals.GetValueOrDefault(sessionId) ?? [];
        lock (list)
            return Task.FromResult<IReadOnlyList<SavedTerminal>>([.. list.OrderBy(t => t.CreatedAt)]);
    }

    public Task SaveTerminalAsync(string sessionId, SavedTerminal terminal, CancellationToken ct = default)
    {
        var list = _terminals.GetOrAdd(sessionId, _ => []);
        lock (list)
        {
            list.RemoveAll(t => t.Id == terminal.Id);
            list.Add(terminal);
        }
        return Task.CompletedTask;
    }

    public Task RemoveTerminalAsync(string sessionId, string terminalId, CancellationToken ct = default)
    {
        if (_terminals.TryGetValue(sessionId, out var list))
        {
            lock (list)
                list.RemoveAll(t => t.Id == terminalId);
        }
        History.TryRemove((sessionId, terminalId), out _);
        return Task.CompletedTask;
    }

    public Task<byte[]?> ReadHistoryAsync(string sessionId, string terminalId, CancellationToken ct = default)
        => Task.FromResult(History.TryGetValue((sessionId, terminalId), out var data) ? data : null);

    public Task WriteHistoryAsync(string sessionId, string terminalId, ReadOnlyMemory<byte> history, CancellationToken ct = default)
    {
        History[(sessionId, terminalId)] = history.ToArray();
        return Task.CompletedTask;
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        _terminals.TryRemove(sessionId, out _);
        foreach (var key in History.Keys.Where(k => k.SessionId == sessionId).ToList())
            History.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

public sealed class TerminalServiceTests
{
    private readonly InMemorySessionRepository _sessions = new();

    [Fact]
    public async Task ASessionTheCallerCantSee_IsNotFound()
    {
        var service = NewService(new FleetOptions());

        var list = await service.ListAsync("someone-elses");

        list.Error!.Kind.ShouldBe(TerminalErrorKind.NotFound);
    }

    [Fact]
    public async Task EverythingIsNotFound_WhenTerminalsAreOff()
    {
        _sessions.Seed(new Session { Id = "s1", UserId = "u1", Directory = "/work" });
        var options = new FleetOptions();
        options.Auth.Enabled = true; // hosted: off unless turned on

        var list = await NewService(options).ListAsync("s1");

        list.Error!.Kind.ShouldBe(TerminalErrorKind.NotFound);
    }

    [Fact]
    public async Task HostedFleet_CanTurnTerminalsOnExplicitly()
    {
        _sessions.Seed(new Session { Id = "s1", UserId = "u1", Directory = "/work" });
        var options = new FleetOptions();
        options.Auth.Enabled = true;
        options.Terminal.Enabled = true;

        var list = await NewService(options).ListAsync("s1");

        list.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AnArchivedSession_CantStartAShell()
    {
        _sessions.Seed(new Session { Id = "s1", UserId = "u1", Directory = "/work", RetentionStatus = "archived" });

        var created = await NewService(new FleetOptions()).CreateAsync("s1", 80, 24);

        created.Error!.Kind.ShouldBe(TerminalErrorKind.Unavailable);
        created.Error.Message.ShouldContain("archived");
    }

    [Fact]
    public async Task TheSetupTerminal_IsRefusedInCloudMode_WhereItWouldRunOnTheServer()
    {
        var created = await NewService(new FleetOptions { Cloud = { Enabled = true } }).CreateSetupAsync(80, 24);

        created.Error!.Kind.ShouldBe(TerminalErrorKind.NotFound);
        created.Error.Message.ShouldBe("Harnesses can only be set up from Fleet when it runs on your own computer.");
    }

    [Theory]
    [InlineData("local-user", "setup-local-user")]
    [InlineData("auth0|5f2c", "setup-auth0_5f2c")]
    public void TheSetupTerminal_IsKeptUnderAnIdThatIsSafeAsAFolderName(string userId, string expected)
    {
        TerminalService.SetupSessionId(userId).ShouldBe(expected);
    }

    private TerminalService NewService(FleetOptions options)
    {
        var manager = new TerminalManager(
            new ThrowingPtyFactory(),
            new InMemoryTerminalHistoryStore(),
            new FakeEventBroadcaster(),
            options,
            NullLogger<TerminalManager>.Instance);
        return new TerminalService(_sessions, manager, options, new TestUserContext("u1"));
    }

    private sealed class ThrowingPtyFactory : IPtyFactory
    {
        public Task<IPtyProcess> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default)
            => throw new InvalidOperationException("Not expected to start a shell.");
    }
}
