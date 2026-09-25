using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Browser;

namespace WeaveFleet.Infrastructure.Tests.Browser;

public sealed class SessionScreenshotStoreTests : IDisposable
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10];

    private readonly string _root = Directory.CreateTempSubdirectory("fleet-screenshots-").FullName;
    private readonly SessionScreenshotStore _store;

    public SessionScreenshotStoreTests() => _store = new SessionScreenshotStore(_root, NullLogger<SessionScreenshotStore>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_saved_screenshot_reads_back_under_its_session()
    {
        var id = await _store.SaveAsync("ses-1", Png);

        id.ShouldNotBeNull();
        id.ShouldStartWith("shot_");
        (await _store.ReadAsync("ses-1", id)).ShouldBe(Png);
        File.Exists(Path.Combine(_root, "ses-1", id + ".png")).ShouldBeTrue();
    }

    [Fact]
    public async Task Each_screenshot_gets_its_own_id()
    {
        var first = await _store.SaveAsync("ses-1", Png);
        var second = await _store.SaveAsync("ses-1", [1, 2, 3]);

        second.ShouldNotBe(first);
        (await _store.ReadAsync("ses-1", first!)).ShouldBe(Png);
        (await _store.ReadAsync("ses-1", second!)).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Another_session_does_not_see_the_screenshot()
    {
        var id = await _store.SaveAsync("ses-1", Png);

        (await _store.ReadAsync("ses-2", id!)).ShouldBeNull();
    }

    [Theory]
    [InlineData("ses-1", "shot_missing")]
    [InlineData("ses-1", "../ses-2/shot_x")]
    [InlineData("ses-1", "index")]
    [InlineData("..", "shot_x")]
    [InlineData("ses-1/../ses-2", "shot_x")]
    public async Task Anything_but_a_screenshot_id_in_a_session_reads_as_not_found(string sessionId, string screenshotId)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "index.png"), Png);

        (await _store.ReadAsync(sessionId, screenshotId)).ShouldBeNull();
    }

    [Fact]
    public async Task Deleting_a_session_removes_its_screenshots_and_leaves_the_others()
    {
        var mine = await _store.SaveAsync("ses-1", Png);
        var theirs = await _store.SaveAsync("ses-2", Png);

        await _store.DeleteSessionAsync("ses-1");
        await _store.DeleteSessionAsync("ses-never-had-any");

        (await _store.ReadAsync("ses-1", mine!)).ShouldBeNull();
        Directory.Exists(Path.Combine(_root, "ses-1")).ShouldBeFalse();
        (await _store.ReadAsync("ses-2", theirs!)).ShouldBe(Png);
    }

    [Fact]
    public async Task A_screenshot_that_cannot_be_written_comes_back_without_an_id()
    {
        // A file where the session's folder should be: the save can't create it.
        await File.WriteAllBytesAsync(Path.Combine(_root, "ses-1"), Png);

        (await _store.SaveAsync("ses-1", Png)).ShouldBeNull();
    }

    [Fact]
    public async Task Clearing_old_screenshots_keeps_the_recent_ones_and_drops_emptied_folders()
    {
        var now = DateTimeOffset.UtcNow;
        var old = await _store.SaveAsync("ses-1", Png);
        var recent = await _store.SaveAsync("ses-1", Png);
        var alsoOld = await _store.SaveAsync("ses-2", Png);
        Age("ses-1", old!, now.AddDays(-8));
        Age("ses-1", recent!, now.AddDays(-6));
        Age("ses-2", alsoOld!, now.AddDays(-8));

        var deleted = _store.DeleteOlderThan(now - SessionScreenshotStore.Retention);

        deleted.ShouldBe(2);
        (await _store.ReadAsync("ses-1", old!)).ShouldBeNull();
        (await _store.ReadAsync("ses-1", recent!)).ShouldBe(Png);
        Directory.Exists(Path.Combine(_root, "ses-2")).ShouldBeFalse();
    }

    [Fact]
    public async Task Clearing_old_screenshots_leaves_files_that_are_not_screenshots()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.SaveAsync("ses-1", Png);
        var stranger = Path.Combine(_root, "ses-1", "notes.txt");
        await File.WriteAllTextAsync(stranger, "mine");
        File.SetLastWriteTimeUtc(stranger, now.AddDays(-30).UtcDateTime);

        _store.DeleteOlderThan(now.AddDays(1));

        File.Exists(stranger).ShouldBeTrue();
    }

    [Fact]
    public void Clearing_old_screenshots_before_any_were_taken_does_nothing()
    {
        var store = new SessionScreenshotStore(Path.Combine(_root, "never-made"), NullLogger<SessionScreenshotStore>.Instance);

        store.DeleteOlderThan(DateTimeOffset.UtcNow).ShouldBe(0);
    }

    private void Age(string sessionId, string id, DateTimeOffset when)
        => File.SetLastWriteTimeUtc(Path.Combine(_root, sessionId, id + ".png"), when.UtcDateTime);
}
