using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Infrastructure.Mods.Host;

namespace WeaveFleet.Infrastructure.Tests.Mods.Host;

/// <summary>The draft watcher against real folders: one call per quiet draft, never through a link, nothing after Dispose.</summary>
public sealed class FileModDraftWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    private readonly string _base = Path.Combine(Path.GetTempPath(), $"fleet-watch-{Guid.NewGuid():N}");
    private readonly string _drafts;
    private readonly ConcurrentQueue<ModDraftChange> _calls = new();
    private readonly List<IDisposable> _watchers = [];

    public FileModDraftWatcherTests()
    {
        _drafts = Path.Combine(_base, "user", "drafts");
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        if (Directory.Exists(_base))
            Directory.Delete(_base, recursive: true);
    }

    private IDisposable Watch(string? root = null)
    {
        var watcher = new FileModDraftWatcher(TimeProvider.System, NullLogger<FileModDraftWatcher>.Instance, Debounce)
            .Watch(root ?? _drafts, _calls.Enqueue);
        _watchers.Add(watcher);
        return watcher;
    }

    private string Draft(string session, string name)
    {
        var folder = Path.Combine(_drafts, session, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("The condition didn't come true.");
            await Task.Delay(10);
        }
    }

    /// <summary>Waits for a first call, then for the folders to go quiet, and returns every call so far.</summary>
    private async Task<ModDraftChange[]> Settled()
    {
        await Until(() => !_calls.IsEmpty);
        await Task.Delay(Quiet);
        return _calls.ToArray();
    }

    private void Forget()
    {
        while (_calls.TryDequeue(out _))
        {
        }
    }

    private static ModDraftChange Change(string session, string name) => new(session, name);

    [Fact]
    public async Task A_burst_of_writes_fires_once_after_it_goes_quiet()
    {
        var folder = Draft("ses_a", "test-chips");
        Watch();

        for (var i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(folder, "mod.ts"), $"// {i}");

        (await Settled()).ShouldBe([Change("ses_a", "test-chips")]);
    }

    [Fact]
    public async Task Nothing_fires_for_content_that_was_there_before_watching()
    {
        Draft("ses_a", "test-chips");
        Watch();

        await Task.Delay(Quiet);

        _calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_drafts_debounce_independently()
    {
        var one = Draft("ses_a", "test-chips");
        var two = Draft("ses_b", "demo-mod");
        Watch();

        File.WriteAllText(Path.Combine(one, "a.txt"), "1");
        File.WriteAllText(Path.Combine(two, "b.txt"), "2");
        File.WriteAllText(Path.Combine(one, "a.txt"), "3");

        var calls = await Settled();

        calls.OrderBy(c => c.Name, StringComparer.Ordinal).ShouldBe([Change("ses_b", "demo-mod"), Change("ses_a", "test-chips")]);
    }

    [Fact]
    public async Task A_write_in_a_subfolder_of_a_draft_fires()
    {
        var folder = Draft("ses_a", "test-chips");
        Directory.CreateDirectory(Path.Combine(folder, "pages", "deep"));
        Watch();

        File.WriteAllText(Path.Combine(folder, "pages", "deep", "a.html"), "<p>hi</p>");

        (await Settled()).ShouldBe([Change("ses_a", "test-chips")]);
    }

    [Fact]
    public async Task A_folder_made_inside_a_draft_after_watching_is_watched_too()
    {
        var folder = Draft("ses_a", "test-chips");
        Watch();
        Directory.CreateDirectory(Path.Combine(folder, "pages"));
        await Settled();
        Forget();

        File.WriteAllText(Path.Combine(folder, "pages", "a.html"), "<p>hi</p>");

        (await Settled()).ShouldBe([Change("ses_a", "test-chips")]);
    }

    [Fact]
    public async Task A_new_draft_folder_fires()
    {
        Directory.CreateDirectory(Path.Combine(_drafts, "ses_a"));
        Watch();

        Draft("ses_a", "test-chips");

        (await Settled()).ShouldBe([Change("ses_a", "test-chips")]);
    }

    [Fact]
    public async Task A_new_draft_in_a_new_session_fires_and_is_watched()
    {
        Directory.CreateDirectory(_drafts);
        Watch();

        var folder = Draft("ses_new", "test-chips");
        await Settled();
        Forget();
        File.WriteAllText(Path.Combine(folder, "mod.ts"), "x");

        (await Settled()).ShouldBe([Change("ses_new", "test-chips")]);
    }

    [Fact]
    public async Task Deleting_a_draft_fires()
    {
        var folder = Draft("ses_a", "test-chips");
        File.WriteAllText(Path.Combine(folder, "mod.ts"), "x");
        Watch();

        Directory.Delete(folder, recursive: true);

        (await Settled()).ShouldBe([Change("ses_a", "test-chips")]);
    }

    [Fact]
    public async Task Deleting_a_session_fires_for_each_of_its_drafts()
    {
        Draft("ses_a", "test-chips");
        Draft("ses_a", "demo-mod");
        Draft("ses_b", "other-mod");
        Watch();

        Directory.Delete(Path.Combine(_drafts, "ses_a"), recursive: true);

        var calls = await Settled();

        calls.Distinct().OrderBy(c => c.Name, StringComparer.Ordinal).ShouldBe([Change("ses_a", "demo-mod"), Change("ses_a", "test-chips")]);
    }

    [Fact]
    public async Task Renaming_a_session_away_fires_for_each_of_its_drafts()
    {
        Draft("ses_a", "test-chips");
        Watch();

        Directory.Move(Path.Combine(_drafts, "ses_a"), Path.Combine(_base, "moved-away"));

        (await Settled()).ShouldContain(Change("ses_a", "test-chips"));
    }

    [Fact]
    public async Task A_rename_inside_a_draft_fires()
    {
        var folder = Draft("ses_a", "test-chips");
        File.WriteAllText(Path.Combine(folder, "old.ts"), "x");
        Watch();

        File.Move(Path.Combine(folder, "old.ts"), Path.Combine(folder, "new.ts"));

        (await Settled()).ShouldBe([Change("ses_a", "test-chips")]);
    }

    [Fact]
    public async Task A_root_that_appears_later_is_picked_up_and_its_drafts_fire()
    {
        var root = Path.Combine(_base, "late", "deeper", "drafts");
        Watch(root);

        Directory.CreateDirectory(Path.Combine(root, "ses_a", "test-chips"));
        var first = await Settled();
        Forget();
        File.WriteAllText(Path.Combine(root, "ses_a", "test-chips", "mod.ts"), "x");
        var second = await Settled();

        first.ShouldContain(Change("ses_a", "test-chips"));
        second.ShouldBe([Change("ses_a", "test-chips")]);
    }

    [Fact]
    public async Task A_root_with_nothing_above_it_stays_quiet_and_does_not_throw()
    {
        Watch(Path.Combine(_base, "no", "such", "place", "drafts"));

        await Task.Delay(Quiet);

        _calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deleted_root_fires_for_the_drafts_it_held()
    {
        Draft("ses_a", "test-chips");
        Watch();

        Directory.Delete(_drafts, recursive: true);

        (await Settled()).ShouldContain(Change("ses_a", "test-chips"));
    }

    [Trait("Category", "ModsFileSafety")]
    [Fact]
    public async Task Writes_in_a_folder_a_link_points_at_do_not_fire()
    {
        if (OperatingSystem.IsWindows())
            return;
        var folder = Draft("ses_a", "test-chips");
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "inner"));
        Directory.CreateSymbolicLink(Path.Combine(folder, "link"), outside);
        Directory.CreateSymbolicLink(Path.Combine(_drafts, "ses_a", "linked-draft"), outside);
        Directory.CreateSymbolicLink(Path.Combine(_drafts, "ses_linked"), outside);
        Watch();
        await Task.Delay(Quiet);

        File.WriteAllText(Path.Combine(outside, "a.txt"), "1");
        File.WriteAllText(Path.Combine(outside, "inner", "b.txt"), "2");
        Directory.CreateDirectory(Path.Combine(outside, "newdir"));
        await Task.Delay(Quiet);

        _calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Names_that_are_not_sessions_or_mods_are_ignored()
    {
        Directory.CreateDirectory(_drafts);
        Watch();

        Directory.CreateDirectory(Path.Combine(_drafts, "not a session", "test-chips"));
        Directory.CreateDirectory(Path.Combine(_drafts, "ses_a", "Bad Name"));
        File.WriteAllText(Path.Combine(_drafts, "stray.txt"), "x");
        File.WriteAllText(Path.Combine(_drafts, "ses_a", "off.json"), "{}");
        await Task.Delay(Quiet);

        _calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_fires_after_Dispose_and_Dispose_twice_is_fine()
    {
        var folder = Draft("ses_a", "test-chips");
        var watcher = Watch();
        File.WriteAllText(Path.Combine(folder, "mod.ts"), "x");
        watcher.Dispose();
        watcher.Dispose();
        await Task.Delay(Quiet);
        Forget();

        File.WriteAllText(Path.Combine(folder, "mod.ts"), "y");
        Draft("ses_a", "demo-mod");
        await Task.Delay(Quiet);

        _calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Changed_is_never_running_twice_at_once_for_the_same_draft()
    {
        var folder = Draft("ses_a", "test-chips");
        var running = 0;
        var overlapped = false;
        var calls = 0;
        _watchers.Add(new FileModDraftWatcher(TimeProvider.System, NullLogger<FileModDraftWatcher>.Instance, Debounce).Watch(_drafts, _ =>
        {
            if (Interlocked.Increment(ref running) > 1)
                overlapped = true;
            Thread.Sleep(200);
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref calls);
        }));

        for (var i = 0; i < 8; i++)
        {
            File.WriteAllText(Path.Combine(folder, "mod.ts"), $"// {i}");
            await Task.Delay(70);
        }

        await Until(() => Volatile.Read(ref calls) >= 2);
        await Task.Delay(Quiet);

        overlapped.ShouldBeFalse();
    }
}
