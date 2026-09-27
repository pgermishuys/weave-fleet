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

        await _store.WriteContextAsync("local-user", folder + "/", folder, "# Fleet memory\n");

        var context = _store.ContextFolder("local-user");
        File.ReadAllText(Path.Combine(context, FileMemoryStore.Hash(folder + "/") + ".md")).ShouldBe("# Fleet memory\n");
        File.ReadAllText(Path.Combine(context, FileMemoryStore.Hash(folder) + ".md")).ShouldBe("# Fleet memory\n");
        (await _store.ListContextFoldersAsync("local-user")).ShouldBe(
            [new MemoryContextFolder(folder + "/", folder), new MemoryContextFolder(folder, folder)], ignoreOrder: true);

        await _store.ClearContextAsync("local-user");

        Directory.Exists(context).ShouldBeFalse();
        (await _store.ListContextFoldersAsync("local-user")).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_machines_notes_are_one_file_every_folder_shares_and_are_cleared_with_the_rest()
    {
        await _store.WriteMachineContextAsync("local-user", "\n## This machine\n- [aaaa0001] Machine. (27 Sep 2026)\n");

        var path = Path.Combine(_store.ContextFolder("local-user"), "machine.md");
        File.ReadAllText(path).ShouldContain("[aaaa0001] Machine.");

        await _store.ClearContextAsync("local-user");

        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task The_folder_index_survives_a_restart_and_a_forgotten_folder_loses_its_file()
    {
        var folder = Path.Combine(_data, "repo");
        await _store.WriteContextAsync("local-user", folder, folder, "notes");
        using var restarted = new FileMemoryStore(new FleetOptions { DatabasePath = Path.Combine(_data, "fleet.db") }, NullLogger<FileMemoryStore>.Instance);

        (await restarted.ListContextFoldersAsync("local-user")).ShouldBe([new MemoryContextFolder(folder, folder)]);

        await restarted.ForgetContextAsync("local-user", folder);

        (await restarted.ListContextFoldersAsync("local-user")).ShouldBeEmpty();
        File.Exists(Path.Combine(restarted.ContextFolder("local-user"), FileMemoryStore.Hash(folder) + ".md")).ShouldBeFalse();
    }

    [Fact]
    public async Task Machine_notes_and_each_repositorys_notes_are_kept_in_folders_of_their_own()
    {
        await _store.SaveAsync("local-user", Note("aaaa0001", "Machine."));
        await _store.SaveAsync("local-user", RepositoryNote("bbbb0002", "/src/alpha", "Alpha."));
        await _store.SaveAsync("local-user", RepositoryNote("cccc0003", "/src/beta", "Beta."));

        var notes = Directory.GetDirectories(Path.Combine(_data, "memory")).Single();
        File.Exists(Path.Combine(notes, "notes", "machine", "aaaa0001.md")).ShouldBeTrue();
        Directory.GetDirectories(Path.Combine(notes, "notes", "repositories")).Length.ShouldBe(2);
        (await _store.ListForAsync("local-user", "/src/alpha")).Select(note => note.Id).ShouldBe(["aaaa0001", "bbbb0002"], ignoreOrder: true);
        (await _store.ListForAsync("local-user", null)).Select(note => note.Id).ShouldBe(["aaaa0001"]);
        (await _store.ListAsync("local-user")).Count.ShouldBe(3);
        (await _store.FindAsync("local-user", "cccc0003"))!.Text.ShouldBe("Beta.");
    }

    [Fact]
    public async Task A_prompt_parses_nothing_it_already_read_and_never_reads_other_repositories()
    {
        for (var i = 0; i < 20; i++)
            await _store.SaveAsync("local-user", RepositoryNote($"aaaa{i:x4}", "/src/other", $"Other {i}."));
        await _store.SaveAsync("local-user", RepositoryNote("bbbb0001", "/src/alpha", "Alpha."));
        await _store.SaveAsync("local-user", Note("cccc0001", "Machine."));
        using var store = new FileMemoryStore(new FleetOptions { DatabasePath = Path.Combine(_data, "fleet.db") }, NullLogger<FileMemoryStore>.Instance);

        await store.ListForAsync("local-user", "/src/alpha");
        store.ParseCount.ShouldBe(2, "alpha's note and the machine's, not the other repository's 20");

        await store.ListForAsync("local-user", "/src/alpha");
        await store.SaveAsync("local-user", RepositoryNote("bbbb0002", "/src/alpha", "Alpha two."));
        (await store.ListForAsync("local-user", "/src/alpha")).Count.ShouldBe(3);
        store.ParseCount.ShouldBe(2, "unchanged files, and a note it wrote itself, aren't parsed again");
    }

    [Fact]
    public async Task A_note_edited_by_hand_is_read_again()
    {
        await _store.SaveAsync("local-user", Note("aaaa0001", "Before."));
        (await _store.ListForAsync("local-user", null)).Single().Text.ShouldBe("Before.");
        var path = Directory.GetFiles(Path.Combine(_data, "memory"), "aaaa0001.md", SearchOption.AllDirectories).Single();

        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("Before.", "After, edited by hand.", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        (await _store.ListForAsync("local-user", null)).Single().Text.ShouldBe("After, edited by hand.");
    }

    [Fact]
    public async Task Notes_kept_loose_in_the_notes_folder_move_into_their_lists_folder()
    {
        await _store.SaveAsync("local-user", Note("aaaa0001", "Machine."));
        var notes = Path.Combine(Directory.GetDirectories(Path.Combine(_data, "memory")).Single(), "notes");
        await File.WriteAllTextAsync(Path.Combine(notes, "bbbb0002.md"), FileMemoryStore.Format(RepositoryNote("bbbb0002", "/src/alpha", "Loose.")));
        await File.WriteAllTextAsync(Path.Combine(notes, "cccc0003.md"), "not a note");
        using var store = new FileMemoryStore(new FleetOptions { DatabasePath = Path.Combine(_data, "fleet.db") }, NullLogger<FileMemoryStore>.Instance);

        (await store.ListForAsync("local-user", "/src/alpha")).Select(note => note.Id).ShouldBe(["aaaa0001", "bbbb0002"], ignoreOrder: true);
        File.Exists(Path.Combine(notes, "bbbb0002.md")).ShouldBeFalse();
        File.Exists(Path.Combine(notes, "cccc0003.md")).ShouldBeTrue("a file that isn't a note stays where it is");
    }

    private static MemoryNote Note(string id, string text)
        => new(id, MemoryList.Machine, text, MemoryKinds.Learned, null, null, null, Saved, Saved);

    private static MemoryNote RepositoryNote(string id, string repository, string text)
        => new(id, MemoryList.Repository, text, MemoryKinds.Learned, repository, null, null, Saved, Saved);
}
