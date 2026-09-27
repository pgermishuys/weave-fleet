using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Memory;
using WeaveFleet.Infrastructure.Memory;

namespace WeaveFleet.Infrastructure.Tests.Memory;

public sealed class FileMemoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Saved = new(2026, 9, 27, 8, 30, 0, TimeSpan.Zero);

    private readonly string _data = Directory.CreateTempSubdirectory("fleet-memory-store-").FullName;
    private readonly FileMemoryStore _store;

    public FileMemoryStoreTests()
    {
        _store = new FileMemoryStore(new FleetOptions { DatabasePath = Path.Combine(_data, "fleet.db") }, NullLogger<FileMemoryStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_data, recursive: true);
    }

    [Fact]
    public async Task A_note_is_kept_as_a_readable_markdown_file_and_read_back_as_it_was()
    {
        var note = new MemoryNote("1a2b3c4d", MemoryList.Repository, "Run E2E with --filter.\nThe full suite takes 12 minutes.", MemoryKinds.Learned,
            "/home/p/source/weave-fleet", "session-1", "Fix the\nflaky test", Saved, Saved.AddHours(1));

        await _store.SaveAsync("local-user", note);

        var file = Directory.GetFiles(Path.Combine(_data, "memory"), "1a2b3c4d.md", SearchOption.AllDirectories).Single();
        (await File.ReadAllTextAsync(file)).ShouldBe(
            "---\nlist: repository\nkind: learned\nrepository: /home/p/source/weave-fleet\nsession: session-1\nsession-title: Fix the flaky test\n"
            + "created: 2026-09-27T08:30:00.0000000+00:00\nupdated: 2026-09-27T09:30:00.0000000+00:00\n---\n"
            + "Run E2E with --filter.\nThe full suite takes 12 minutes.\n");
        (await _store.ListAsync("local-user")).Single().ShouldBe(note with { SessionTitle = "Fix the flaky test" });
        (await _store.ListAsync("someone-else")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_that_isnt_a_note_is_skipped_and_left_alone()
    {
        await _store.SaveAsync("local-user", Note("aaaa0001", "Kept."));
        var folder = Path.GetDirectoryName(Directory.GetFiles(Path.Combine(_data, "memory"), "aaaa0001.md", SearchOption.AllDirectories).Single())!;
        await File.WriteAllTextAsync(Path.Combine(folder, "bbbb0002.md"), "just some text");
        await File.WriteAllTextAsync(Path.Combine(folder, "README.md"), "---\nlist: machine\n---\nNot a note id.\n");

        (await _store.ListAsync("local-user")).Select(note => note.Id).ShouldBe(["aaaa0001"]);
        File.Exists(Path.Combine(folder, "bbbb0002.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task Ids_that_could_name_another_path_are_refused()
    {
        await Should.ThrowAsync<ArgumentException>(() => _store.SaveAsync("local-user", Note("../escape", "No.")));
        (await _store.DeleteAsync("local-user", ["../../fleet.db"])).ShouldBe(0);
        FileMemoryStore.IsValidId("0123abcd").ShouldBeTrue();
        FileMemoryStore.IsValidId("0123ABCD").ShouldBeFalse();
    }

    [Fact]
    public async Task Deleting_removes_only_the_named_notes()
    {
        await _store.SaveAsync("local-user", Note("aaaa0001", "One."));
        await _store.SaveAsync("local-user", Note("aaaa0002", "Two."));

        (await _store.DeleteAsync("local-user", ["aaaa0001", "ffff9999"])).ShouldBe(1);

        (await _store.ListAsync("local-user")).Select(note => note.Id).ShouldBe(["aaaa0002"]);
    }

    [Fact]
    public void A_folders_file_is_named_by_the_sha256_the_plugins_compute()
    {
        // `printf '%s' /home/p/source/weave-fleet | sha256sum`, which node's createHash("sha256") matches.
        FileMemoryStore.Hash("/home/p/source/weave-fleet").ShouldBe("e9ecdb4d6dc4d0bc7e1fd0ff0e50d432d1291158be3368f86da21ffba004b408");
    }

    [Fact]
    public async Task What_a_folder_reads_is_written_under_its_path_and_its_trimmed_path_and_cleared_when_memory_is_off()
    {
        var folder = Path.Combine(_data, "repo");

        await _store.WriteContextAsync("local-user", folder + "/", "# Fleet memory\n");

        var context = _store.ContextFolder("local-user");
        File.ReadAllText(Path.Combine(context, FileMemoryStore.Hash(folder + "/") + ".md")).ShouldBe("# Fleet memory\n");
        File.ReadAllText(Path.Combine(context, FileMemoryStore.Hash(folder) + ".md")).ShouldBe("# Fleet memory\n");
        (await _store.ListContextDirectoriesAsync("local-user")).ShouldBe([folder + "/", folder], ignoreOrder: true);

        await _store.ClearContextAsync("local-user");

        Directory.Exists(context).ShouldBeFalse();
        (await _store.ListContextDirectoriesAsync("local-user")).ShouldBeEmpty();
    }

    private static MemoryNote Note(string id, string text)
        => new(id, MemoryList.Machine, text, MemoryKinds.Learned, null, null, null, Saved, Saved);
}
