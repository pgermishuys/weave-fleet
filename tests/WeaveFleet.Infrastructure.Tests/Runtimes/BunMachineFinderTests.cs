using System.Collections.Concurrent;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Infrastructure.Runtimes;

namespace WeaveFleet.Infrastructure.Tests.Runtimes;

public sealed class BunMachineFinderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fleet-find-{Guid.NewGuid():N}");
    private readonly ConcurrentDictionary<string, BunProbeResult> _answers = new();
    private readonly ConcurrentQueue<string> _probed = new();

    public BunMachineFinderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FileName(bool windows = false) => windows ? "bun.exe" : "bun";

    private string Bun(string folder, bool windows = false, string? version = "1.4.5")
    {
        var path = Path.Combine(Folder(folder), FileName(windows));
        File.WriteAllText(path, "not a real bun");
        if (version is not null)
            _answers[path] = new BunProbeResult(BunVersion.Parse(version), null);
        return path;
    }

    private BunMachineFinder Finder(
        string? pathEnv = null,
        bool windows = false,
        IReadOnlyList<string>? system = null) => new()
    {
        Home = Path.Combine(_root, "home"),
        PathEnv = pathEnv,
        IsWindows = windows,
        SystemDirectories = system ?? [],
        Probe = (path, _) =>
        {
            _probed.Enqueue(path);
            return Task.FromResult(_answers.TryGetValue(path, out var result)
                ? result
                : new BunProbeResult(null, "It didn't run."));
        },
    };

    private static string Join(params string[] folders) => string.Join(Path.PathSeparator, folders);

    private static async Task<IReadOnlyList<string>> Paths(BunMachineFinder finder) =>
        (await finder.FindAsync(CancellationToken.None)).Select(c => c.Path).ToList();

    [Fact]
    public async Task Finds_bun_in_a_PATH_folder()
    {
        var bun = Bun("one");

        var found = await Finder(Join(Folder("one"))).FindAsync(CancellationToken.None);

        found.ShouldBe([new BunCandidate(bun, bun, "1.4.5", BunCandidateStatuses.Usable, null)]);
    }

    [Fact]
    public async Task Finds_bun_in_the_home_bun_folder()
    {
        var bun = Bun(Path.Combine("home", ".bun", "bin"));

        (await Paths(Finder(null))).ShouldBe([bun]);
    }

    [Fact]
    public async Task Finds_bun_in_each_system_folder()
    {
        var a = Bun("sys-a");
        var b = Bun("sys-b");

        (await Paths(Finder(null, system: [Folder("sys-a"), Folder("sys-b")]))).ShouldBe([a, b]);
    }

    [Fact]
    public void The_system_folders_are_homebrew_and_usr_local()
    {
        var finder = new BunMachineFinder { Home = _root };

        finder.SystemDirectories.ShouldBe(["/opt/homebrew/bin", "/usr/local/bin"]);
    }

    [Fact]
    public async Task Searches_PATH_then_home_then_system_folders()
    {
        var system = Bun("sys");
        var home = Bun(Path.Combine("home", ".bun", "bin"));
        var second = Bun("p2");
        var first = Bun("p1");

        var paths = await Paths(Finder(Join(Folder("p1"), Folder("p2")), system: [Folder("sys")]));

        paths.ShouldBe([first, second, home, system]);
    }

    [Fact]
    public async Task Ignores_relative_and_empty_PATH_entries()
    {
        Bun("one");
        // A relative entry that does lead to a bun from the current folder: it must still be ignored.
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), Folder("one"));
        Path.IsPathRooted(relative).ShouldBeFalse();
        var finder = Finder($".{Path.PathSeparator}{Path.PathSeparator}{relative}");

        (await Paths(finder)).ShouldBeEmpty();
    }

    [Fact]
    public async Task One_file_reached_through_two_links_is_reported_once_at_the_first_path()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Bun("real");
        var first = Path.Combine(Folder("a"), "bun");
        var second = Path.Combine(Folder("b"), "bun");
        File.CreateSymbolicLink(first, target);
        File.CreateSymbolicLink(second, target);

        var found = await Finder(Join(Folder("a"), Folder("b"), Folder("real"))).FindAsync(CancellationToken.None);

        var only = found.ShouldHaveSingleItem();
        only.Path.ShouldBe(first);
        only.ResolvedPath.ShouldBe(target);
        _probed.ShouldBe([first]);
    }

    [Fact]
    public async Task A_folder_that_is_a_link_to_another_folder_is_the_same_file()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Bun("real");
        Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), Folder("real"));

        var found = await Finder(Join(Folder("real"), Path.Combine(_root, "alias"))).FindAsync(CancellationToken.None);

        found.ShouldHaveSingleItem().Path.ShouldBe(target);
    }

    [Fact]
    public async Task A_broken_link_is_skipped()
    {
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(Path.Combine(Folder("a"), "bun"), Path.Combine(_root, "gone"));

        (await Paths(Finder(Join(Folder("a"))))).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_folder_named_bun_is_skipped()
    {
        Directory.CreateDirectory(Path.Combine(Folder("a"), FileName()));

        (await Paths(Finder(Join(Folder("a"))))).ShouldBeEmpty();
    }

    [Fact]
    public async Task On_Windows_only_bun_exe_counts_and_the_separator_is_a_semicolon()
    {
        var exe = Bun("a", windows: true);
        File.WriteAllText(Path.Combine(Folder("b"), "bun.cmd"), "x");
        File.WriteAllText(Path.Combine(Folder("b"), "bun.bat"), "x");
        File.WriteAllText(Path.Combine(Folder("b"), "bun"), "x");
        var c = Bun("c", windows: true);

        var finder = Finder($"{Folder("a")};{Folder("b")};{Folder("c")}", windows: true);

        (await Paths(finder)).ShouldBe([exe, c]);
    }

    [Fact]
    public async Task On_Windows_the_home_folder_is_searched_and_the_system_folders_are_not()
    {
        var home = Bun(Path.Combine("home", ".bun", "bin"), windows: true);
        Bun("sys", windows: true);

        (await Paths(Finder(null, windows: true, system: [Folder("sys")]))).ShouldBe([home]);
    }

    [Fact]
    public async Task Probes_every_file_and_keeps_search_order()
    {
        var a = Bun("a");
        var b = Bun("b");
        var c = Bun("c");
        var finder = new BunMachineFinder
        {
            Home = Path.Combine(_root, "home"),
            PathEnv = Join(Folder("a"), Folder("b"), Folder("c")),
            SystemDirectories = [],
            Probe = async (path, _) =>
            {
                // The first answers last: results must still come back in search order.
                await Task.Delay(path == a ? 150 : 0, CancellationToken.None);
                return new BunProbeResult(BunVersion.Parse("1.5.0"), null);
            },
        };

        (await Paths(finder)).ShouldBe([a, b, c]);
    }

    [Fact]
    public async Task Classifies_usable_too_old_and_not_working()
    {
        var usable = Bun("usable", version: "1.4.0");
        var old = Bun("old", version: "1.3.9");
        var broken = Bun("broken", version: null);
        _answers[broken] = new BunProbeResult(null, "It exited with code 3.");
        var canary = Bun("canary", version: "1.4.0-canary.1");

        var found = await Finder(Join(Folder("usable"), Folder("old"), Folder("broken"), Folder("canary")))
            .FindAsync(CancellationToken.None);

        found.ShouldBe(
        [
            new BunCandidate(usable, usable, "1.4.0", BunCandidateStatuses.Usable, null),
            new BunCandidate(
                old, old, "1.3.9", BunCandidateStatuses.TooOld,
                "Bun 1.3.9 is older than 1.4.0, the oldest Bun mods run on. Run bun upgrade to update it."),
            new BunCandidate(broken, broken, null, BunCandidateStatuses.NotWorking, "It exited with code 3."),
            new BunCandidate(
                canary, canary, "1.4.0-canary.1", BunCandidateStatuses.TooOld,
                "Bun 1.4.0-canary.1 is older than 1.4.0, the oldest Bun mods run on. Run bun upgrade to update it."),
        ]);
    }

    [Fact]
    public async Task Finds_nothing_when_there_is_no_bun()
    {
        (await Paths(Finder(Join(Folder("empty"))))).ShouldBeEmpty();
        (await Paths(Finder(null))).ShouldBeEmpty();
    }

    [Fact]
    public async Task CheckAsync_rejects_a_relative_path()
    {
        var candidate = await Finder().CheckAsync("bun", CancellationToken.None);

        candidate.Status.ShouldBe(BunCandidateStatuses.NotWorking);
        candidate.Version.ShouldBeNull();
        candidate.Message.ShouldNotBeNull();
        candidate.Message.ShouldStartWith("Use the full path to bun");
        _probed.ShouldBeEmpty();
    }

    [Fact]
    public async Task CheckAsync_reports_a_missing_file()
    {
        var path = Path.Combine(_root, "nothing", FileName(OperatingSystem.IsWindows()));

        var candidate = await Finder().CheckAsync(path, CancellationToken.None);

        candidate.ShouldBe(new BunCandidate(
            path, path, null, BunCandidateStatuses.NotWorking, $"There's no file at {path}."));
        _probed.ShouldBeEmpty();
    }

    [Fact]
    public async Task CheckAsync_reports_a_folder_as_no_file()
    {
        var folder = Folder("a");

        var candidate = await Finder().CheckAsync(folder, CancellationToken.None);

        candidate.Status.ShouldBe(BunCandidateStatuses.NotWorking);
        candidate.Message.ShouldBe($"There's no file at {folder}.");
        _probed.ShouldBeEmpty();
    }

    [Fact]
    public async Task CheckAsync_reports_a_too_old_bun()
    {
        var bun = Bun("a", version: "1.2.0");

        var candidate = await Finder().CheckAsync(bun, CancellationToken.None);

        candidate.ShouldBe(new BunCandidate(
            bun, bun, "1.2.0", BunCandidateStatuses.TooOld,
            "Bun 1.2.0 is older than 1.4.0, the oldest Bun mods run on. Run bun upgrade to update it."));
    }

    [Fact]
    public async Task CheckAsync_accepts_a_usable_bun_and_keeps_the_typed_path()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Bun("real");
        var link = Path.Combine(Folder("a"), "bun");
        File.CreateSymbolicLink(link, target);

        _answers[link] = new BunProbeResult(BunVersion.Parse("1.4.5"), null);

        var candidate = await Finder().CheckAsync(link, CancellationToken.None);

        candidate.ShouldBe(new BunCandidate(link, target, "1.4.5", BunCandidateStatuses.Usable, null));
    }

    [Fact]
    public async Task CheckAsync_passes_on_why_a_bun_is_not_working()
    {
        var bun = Bun("a", version: null);
        _answers[bun] = new BunProbeResult(null, "Bun didn't answer within 5 seconds.");

        var candidate = await Finder().CheckAsync(bun, CancellationToken.None);

        candidate.Status.ShouldBe(BunCandidateStatuses.NotWorking);
        candidate.Message.ShouldBe("Bun didn't answer within 5 seconds.");
    }

    [Fact]
    public async Task End_to_end_runs_real_scripts()
    {
        if (OperatingSystem.IsWindows()) return;
        var good = WriteScript("good", "echo 1.4.9");
        var old = WriteScript("old", "echo 1.3.0");
        var broken = WriteScript("broken", "exit 2");

        var found = await new BunMachineFinder
        {
            Home = Path.Combine(_root, "home"),
            PathEnv = Join(Folder("good"), Folder("old"), Folder("broken")),
            SystemDirectories = [],
        }.FindAsync(CancellationToken.None);

        found.Select(c => (c.Path, c.Status)).ShouldBe(
        [
            (good, BunCandidateStatuses.Usable),
            (old, BunCandidateStatuses.TooOld),
            (broken, BunCandidateStatuses.NotWorking),
        ]);
        found[0].Version.ShouldBe("1.4.9");
    }

    [Fact]
    public async Task End_to_end_check_runs_the_real_probe()
    {
        if (OperatingSystem.IsWindows()) return;
        var good = WriteScript("good", "echo 1.4.9");

        var candidate = await new BunMachineFinder { Home = _root }.CheckAsync(good, CancellationToken.None);

        candidate.Status.ShouldBe(BunCandidateStatuses.Usable);
        candidate.Version.ShouldBe("1.4.9");
    }

    private string WriteScript(string folder, string body)
    {
        var path = Path.Combine(Folder(folder), "bun");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
