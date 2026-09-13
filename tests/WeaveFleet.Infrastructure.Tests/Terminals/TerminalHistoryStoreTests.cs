using System.Text;
using WeaveFleet.Application.Terminals;
using WeaveFleet.Infrastructure.Terminals;

namespace WeaveFleet.Infrastructure.Tests.Terminals;

public sealed class TerminalHistoryStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fleet-terminals-").FullName;
    private readonly TerminalHistoryStore _store;

    public TerminalHistoryStoreTests() => _store = new TerminalHistoryStore(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task History_RoundTrips()
    {
        await _store.WriteHistoryAsync("s1", "t1", Encoding.UTF8.GetBytes("hello ✓\n"));

        var read = await _store.ReadHistoryAsync("s1", "t1");

        Encoding.UTF8.GetString(read!).ShouldBe("hello ✓\n");
    }

    [Fact]
    public async Task History_IsNull_WhenNothingWasSaved()
    {
        (await _store.ReadHistoryAsync("s1", "missing")).ShouldBeNull();
    }

    [Fact]
    public async Task WriteHistory_LeavesNoTemporaryFiles()
    {
        await _store.WriteHistoryAsync("s1", "t1", "a"u8.ToArray());
        await _store.WriteHistoryAsync("s1", "t1", "b"u8.ToArray());

        Directory.GetFiles(Path.Combine(_root, "s1")).Select(Path.GetFileName).ShouldBe(["t1.log"]);
        Encoding.UTF8.GetString((await _store.ReadHistoryAsync("s1", "t1"))!).ShouldBe("b");
    }

    [Fact]
    public async Task List_IsOldestFirst_AndSaveUpdatesInPlace()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.SaveTerminalAsync("s1", new SavedTerminal("t2", "zsh 2", now.AddSeconds(1)));
        await _store.SaveTerminalAsync("s1", new SavedTerminal("t1", "zsh", now));
        await _store.SaveTerminalAsync("s1", new SavedTerminal("t2", "bash", now.AddSeconds(1)));

        var list = await _store.ListAsync("s1");

        list.Select(t => (t.Id, t.Title)).ShouldBe([("t1", "zsh"), ("t2", "bash")]);
    }

    [Fact]
    public async Task List_IsEmpty_ForAnUnknownSession()
    {
        (await _store.ListAsync("nobody")).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_IsEmpty_WhenTheIndexIsDamaged()
    {
        Directory.CreateDirectory(Path.Combine(_root, "s1"));
        await File.WriteAllTextAsync(Path.Combine(_root, "s1", "index.json"), "{not json");

        (await _store.ListAsync("s1")).ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveTerminal_DropsItFromTheListAndDeletesItsHistory()
    {
        await _store.SaveTerminalAsync("s1", new SavedTerminal("t1", "zsh", DateTimeOffset.UtcNow));
        await _store.SaveTerminalAsync("s1", new SavedTerminal("t2", "zsh 2", DateTimeOffset.UtcNow));
        await _store.WriteHistoryAsync("s1", "t1", "x"u8.ToArray());

        await _store.RemoveTerminalAsync("s1", "t1");

        (await _store.ListAsync("s1")).Select(t => t.Id).ShouldBe(["t2"]);
        (await _store.ReadHistoryAsync("s1", "t1")).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteSession_RemovesEverything()
    {
        await _store.SaveTerminalAsync("s1", new SavedTerminal("t1", "zsh", DateTimeOffset.UtcNow));
        await _store.WriteHistoryAsync("s1", "t1", "x"u8.ToArray());

        await _store.DeleteSessionAsync("s1");
        await _store.DeleteSessionAsync("s1");

        Directory.Exists(Path.Combine(_root, "s1")).ShouldBeFalse();
    }

    [Fact]
    public async Task ConcurrentSaves_AllLand()
    {
        var now = DateTimeOffset.UtcNow;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            _store.SaveTerminalAsync("s1", new SavedTerminal($"t{i}", "zsh", now.AddMilliseconds(i)))));

        (await _store.ListAsync("s1")).Count.ShouldBe(20);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task RejectsIdsThatAreNotPlainFileNames(string id)
    {
        await Should.ThrowAsync<ArgumentException>(() => _store.ReadHistoryAsync(id, "t1"));
        await Should.ThrowAsync<ArgumentException>(() => _store.ReadHistoryAsync("s1", id));
    }
}
